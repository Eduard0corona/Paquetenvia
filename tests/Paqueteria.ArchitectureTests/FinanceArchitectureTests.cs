using Paqueteria.ArchitectureTests.Architecture;

namespace Paqueteria.ArchitectureTests;

public sealed class FinanceArchitectureTests
{
    [Fact]
    public void Finance_has_exactly_the_four_canonical_layers()
    {
        Assert.Equal(
            ["Finance.Domain", "Finance.Application", "Finance.Infrastructure", "Finance.Endpoints"],
            SolutionCatalog.Finance.Components.Select(component => component.Name));
    }

    [Fact]
    public void Domain_and_application_are_free_of_technical_storage_dependencies()
    {
        var forbidden = new[] { "Npgsql", "EntityFrameworkCore", "AspNetCore" };
        var references = SolutionCatalog.Finance.Domain.Assembly.GetReferencedAssemblies()
            .Concat(SolutionCatalog.Finance.Application.Assembly.GetReferencedAssemblies())
            .Select(reference => reference.Name ?? string.Empty);

        Assert.DoesNotContain(references, reference =>
            forbidden.Any(value => reference.Contains(value, StringComparison.OrdinalIgnoreCase)));
        Assert.Empty(ProjectMetadataReader.Read(SolutionCatalog.Finance.Domain).PackageReferences);
        Assert.Empty(ProjectMetadataReader.Read(SolutionCatalog.Finance.Domain).FrameworkReferences);
    }

    [Fact]
    public void Finance_only_depends_on_organizations_across_module_boundaries()
    {
        // FIN-001 reads orders, dispatch and routes through SQL against the canonical schema rather than
        // by referencing those modules, so unit economics cannot drift into their business logic.
        Assert.Equal(
            ["Organizations"],
            SolutionCatalog.Finance.AllowedCrossModuleDependencies.Order(StringComparer.Ordinal));

        foreach (var component in SolutionCatalog.Finance.Components)
        {
            var referenced = ProjectMetadataReader.Read(component).ProjectReferencePaths
                .Where(SolutionCatalog.ByProjectPath.ContainsKey)
                .Select(path => SolutionCatalog.ByProjectPath[path])
                .Where(project => project.ModuleName is not null and not "Finance")
                .Select(project => project.ModuleName!)
                .Distinct(StringComparer.Ordinal);
            Assert.All(referenced, module => Assert.Equal("Organizations", module));
        }
    }

    [Fact]
    public void Finance_owns_no_schema_migration_because_the_canonical_baseline_already_defines_it()
    {
        // finance.cod_transactions ships in AI-06, so FIN-001 adds no migration of its own.
        Assert.False(Directory.Exists(TestRepository.GetPath(
            "src/Modules/Finance/Finance.Infrastructure/Persistence/Migrations")));
    }

    [Fact]
    public void Money_never_leaves_the_finance_module_as_binary_floating_point()
    {
        var offenders = SolutionCatalog.Finance.Components
            .SelectMany(component => component.Assembly.GetTypes())
            .Where(type => type.IsPublic)
            .SelectMany(type => type.GetProperties().Select(property => (type, property)))
            .Where(pair =>
            {
                var propertyType = Nullable.GetUnderlyingType(pair.property.PropertyType)
                    ?? pair.property.PropertyType;
                return propertyType == typeof(float) ||
                    propertyType == typeof(double) ||
                    propertyType == typeof(decimal);
            })
            .Select(pair => $"{pair.type.FullName}.{pair.property.Name}")
            .ToArray();

        Assert.Empty(offenders);
    }
}
