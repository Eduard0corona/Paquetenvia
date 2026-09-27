using Identity.Application.Authentication;
using Identity.Application.Bootstrap;
using Identity.Endpoints.Testing;
using Identity.Infrastructure.Bootstrap;
using Identity.Infrastructure.Mock;
using Orders.Application.Tracking;
using Orders.Infrastructure.Tracking;
using Paqueteria.Contracts.Tracking;
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
    public void Bootstrap_and_tracking_ports_are_framework_and_Npgsql_independent()
    {
        Assert.Equal("Identity.Application", typeof(IIdentityContextResolver).Assembly.GetName().Name);
        Assert.Contains(typeof(IIdentityContextResolver), typeof(PostgreSqlIdentityContextResolver).GetInterfaces());
        Assert.Equal("Identity.Infrastructure", typeof(PostgreSqlIdentityContextResolver).Assembly.GetName().Name);
        Assert.Equal("Orders.Application", typeof(IPublicTrackingProjectionReader).Assembly.GetName().Name);
        Assert.Contains(typeof(IPublicTrackingProjectionReader), typeof(PostgreSqlPublicTrackingProjectionReader).GetInterfaces());
        Assert.Equal("Orders.Infrastructure", typeof(PostgreSqlPublicTrackingProjectionReader).Assembly.GetName().Name);

        foreach (var assembly in new[] { typeof(IIdentityContextResolver).Assembly, typeof(IPublicTrackingProjectionReader).Assembly })
        {
            var references = assembly.GetReferencedAssemblies().Select(reference => reference.Name ?? string.Empty);
            Assert.DoesNotContain(references, name =>
                name.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("AspNetCore", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void External_identity_contains_no_tenant_authorization_data()
    {
        // Authentication evidence only: subject, MFA and verified email (AUTH-EMAIL-VERIFIED-REQUIRED).
        Assert.Equal(
            ["EmailVerified", "MfaSatisfied", "Subject"],
            typeof(ExternalIdentity).GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Productive_tracking_utilities_have_single_implementations()
    {
        Assert.Equal("Paqueteria.Contracts", typeof(TrackingTokenHasher).Assembly.GetName().Name);
        Assert.Equal("Orders.Application", typeof(PublicOrderStatusPolicy).Assembly.GetName().Name);
        Assert.Single(
            SolutionCatalog.All.SelectMany(component => component.Assembly.GetTypes()),
            type => type.Name == nameof(TrackingTokenHasher));
        Assert.Single(
            SolutionCatalog.All.SelectMany(component => component.Assembly.GetTypes()),
            type => type.Name == nameof(PublicOrderStatusPolicy));
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

        // ADP-001-PII-KEYVAULT-ENVELOPE / ADP-001-POD-BLOB-DEFENDER: Azure.Identity is admitted only
        // as the workload (managed identity) credential of the building-block infrastructure, never
        // as a user identity provider. Every other component stays under the full ban.
        var packages = SolutionCatalog.All
            .SelectMany(component => ProjectMetadataReader.Read(component).PackageReferences
                .Where(package => component != SolutionCatalog.Infrastructure ||
                    !string.Equals(package, WorkloadCredentialPackage, StringComparison.Ordinal)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.DoesNotContain(packages, package =>
            forbidden.Any(name => package.Contains(name, StringComparison.OrdinalIgnoreCase)));
    }

    private const string WorkloadCredentialPackage = "Azure.Identity";

    [Fact]
    public void Azure_Identity_is_only_the_managed_identity_workload_credential()
    {
        var owners = SolutionCatalog.All
            .Where(component => ProjectMetadataReader.Read(component).PackageReferences.Contains(
                WorkloadCredentialPackage,
                StringComparer.OrdinalIgnoreCase))
            .Select(component => component.Name)
            .ToArray();
        Assert.Equal(["Paqueteria.Infrastructure"], owners);

        // Only one source file may use the namespace, and it builds a ManagedIdentityCredential and
        // nothing that signs a user in or reads a secret.
        var sources = Directory
            .EnumerateFiles(TestRepository.GetPath("src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                           !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains("Azure.Identity", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(TestRepository.GetPath("."), path).Replace('\\', '/'))
            .ToArray();
        Assert.Equal(["src/BuildingBlocks/Paqueteria.Infrastructure/Cloud/AzureWorkloadCredential.cs"], sources);
        var credential = File.ReadAllText(TestRepository.GetPath(sources[0]));
        Assert.Contains("new ManagedIdentityCredential(", credential, StringComparison.Ordinal);
        foreach (var forbiddenCredential in new[]
                 {
                     "DefaultAzureCredential", "InteractiveBrowserCredential", "DeviceCodeCredential",
                     "ClientSecretCredential", "UsernamePasswordCredential", "ClientCertificateCredential",
                     "AuthorizationCodeCredential", "OnBehalfOfCredential", "EnvironmentCredential",
                 })
        {
            Assert.DoesNotContain(forbiddenCredential, credential, StringComparison.Ordinal);
        }

        foreach (var component in SolutionCatalog.Identity.Components)
        {
            Assert.DoesNotContain(component.Assembly.GetReferencedAssemblies(), reference =>
                string.Equals(reference.Name, WorkloadCredentialPackage, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Only_Identity_Endpoints_references_the_standard_OpenIdConnect_handler()
    {
        var owners = SolutionCatalog.All
            .Where(component => ProjectMetadataReader.Read(component).PackageReferences.Contains(
                "Microsoft.AspNetCore.Authentication.OpenIdConnect",
                StringComparer.OrdinalIgnoreCase))
            .Select(component => component.Name)
            .ToArray();

        Assert.Equal(["Identity.Endpoints"], owners);
    }

    [Fact]
    public void AuthCenter_is_a_first_class_provider_without_changing_the_external_identity_contract()
    {
        Assert.True(Enum.IsDefined(IdentityProviderKind.AuthCenter));
        Assert.Equal(
            "Identity.Endpoints",
            typeof(Identity.Endpoints.AuthCenter.AuthCenterDefaults).Assembly.GetName().Name);
        Assert.Equal("__Host-Paquetenvia.Session", Identity.Endpoints.AuthCenter.AuthCenterDefaults.SessionCookieName);
        Assert.Equal("X-AuthCenter-CSRF", Identity.Endpoints.AuthCenter.AuthCenterDefaults.CsrfHeaderName);
        Assert.Equal("/signin-authcenter", Identity.Endpoints.AuthCenter.AuthCenterDefaults.CallbackPath);
    }
}
