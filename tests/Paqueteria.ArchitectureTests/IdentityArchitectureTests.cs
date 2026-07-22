using Identity.Application.Authentication;
using Identity.Endpoints.Testing;
using Identity.Infrastructure.Mock;
using Paqueteria.ArchitectureTests.Architecture;

namespace Paqueteria.ArchitectureTests;

public sealed class IdentityArchitectureTests
{
    [Fact]
    public void Identity_module_has_exactly_the_four_canonical_layers()
    {
        Assert.Equal(
            [
                ProjectRole.ModuleDomain,
                ProjectRole.ModuleApplication,
                ProjectRole.ModuleInfrastructure,
                ProjectRole.ModuleEndpoints,
            ],
            SolutionCatalog.Identity.Components.Select(component => component.Role));
    }

    [Fact]
    public void Infrastructure_mock_implements_the_framework_independent_port()
    {
        Assert.Contains(typeof(IIdentityProvider), typeof(MockIdentityProvider).GetInterfaces());
        Assert.Equal("Identity.Application", typeof(IIdentityProvider).Assembly.GetName().Name);
        Assert.DoesNotContain(
            typeof(IIdentityProvider).Assembly.GetReferencedAssemblies(),
            reference => reference.Name?.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Domain_does_not_reference_authentication_or_AspNetCore()
    {
        var references = typeof(Identity.Domain.AssemblyReference).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(references, reference =>
            reference.Contains("AspNetCore", StringComparison.OrdinalIgnoreCase) ||
            reference.Contains("Authentication", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Security_test_hub_lives_only_in_the_transport_layer()
    {
        Assert.Equal("Identity.Endpoints", typeof(SecurityTestHub).Assembly.GetName().Name);

        foreach (var component in SolutionCatalog.Identity.Components.Where(component =>
                     component.Role is not ProjectRole.ModuleEndpoints))
        {
            Assert.DoesNotContain(component.Assembly.GetTypes(), type =>
                type.Name.EndsWith("Hub", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void No_productive_identity_provider_SDK_is_referenced()
    {
        string[] forbidden =
        [
            "Auth0",
            "Azure.Identity",
            "IdentityModel",
            "Duende",
            "IdentityServer",
            "Keycloak",
            "Cognito",
            "Okta",
            "Clerk",
            "FusionAuth",
        ];

        var packages = SolutionCatalog.All
            .SelectMany(component => ProjectMetadataReader.Read(component).PackageReferences)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.DoesNotContain(packages, package =>
            forbidden.Any(name => package.Contains(name, StringComparison.OrdinalIgnoreCase)));
    }
}
