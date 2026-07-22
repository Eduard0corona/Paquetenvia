using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Organizations.Application.OrganizationContexts;
using Organizations.Infrastructure.OrganizationContexts;
using Organizations.Infrastructure.Persistence;
using Paqueteria.ArchitectureTests.Architecture;

namespace Paqueteria.ArchitectureTests;

public sealed class OrganizationsArchitectureTests
{
    [Fact]
    public void Organizations_module_has_exactly_the_four_canonical_layers()
    {
        Assert.Equal(
            [
                ProjectRole.ModuleDomain,
                ProjectRole.ModuleApplication,
                ProjectRole.ModuleInfrastructure,
                ProjectRole.ModuleEndpoints,
            ],
            SolutionCatalog.Organizations.Components.Select(component => component.Role));
    }

    [Fact]
    public void Identity_and_organizations_have_one_DbContext_each_and_only_in_infrastructure()
    {
        var contexts = SolutionCatalog.All
            .SelectMany(component => component.Assembly.GetTypes())
            .Where(type => type.IsAssignableTo(typeof(DbContext)))
            .ToArray();

        Assert.Single(contexts, type => type == typeof(IdentityDbContext));
        Assert.Single(contexts, type => type == typeof(OrganizationsDbContext));
        Assert.All(contexts.Where(type => type.Namespace?.StartsWith("Identity", StringComparison.Ordinal) == true ||
                                         type.Namespace?.StartsWith("Organizations", StringComparison.Ordinal) == true),
            type => Assert.EndsWith(".Infrastructure", type.Assembly.GetName().Name, StringComparison.Ordinal));
    }

    [Fact]
    public void Organization_context_port_is_framework_and_Npgsql_independent()
    {
        Assert.Equal("Organizations.Application", typeof(IOrganizationContextReader).Assembly.GetName().Name);
        Assert.Contains(typeof(IOrganizationContextReader), typeof(PostgreSqlOrganizationContextReader).GetInterfaces());
        var references = typeof(IOrganizationContextReader).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty);
        Assert.DoesNotContain(references, reference =>
            reference.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) ||
            reference.Contains("AspNetCore", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Worker_does_not_reference_tenant_request_or_organizations_module()
    {
        var metadata = ProjectMetadataReader.Read(SolutionCatalog.Worker);
        Assert.DoesNotContain(metadata.ProjectReferencePaths, reference =>
            reference.Contains("Organizations", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            SolutionCatalog.Worker.Assembly.GetReferencedAssemblies(),
            reference => reference.Name?.Contains("Organizations", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public void Organization_role_has_a_single_productive_definition()
    {
        var definitions = SolutionCatalog.All
            .SelectMany(component => component.Assembly.GetTypes())
            .Where(type => type.Name == "OrganizationRole")
            .ToArray();

        Assert.Single(definitions);
        Assert.Equal("Paqueteria.Domain", definitions[0].Assembly.GetName().Name);
    }
}
