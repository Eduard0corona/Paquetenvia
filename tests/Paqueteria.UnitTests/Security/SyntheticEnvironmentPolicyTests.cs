using Custody.Application.ProofUploads;
using Custody.Infrastructure.ProofStorage;
using Identity.Application.Authentication;
using Identity.Endpoints;
using Locations.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Paqueteria.Application.Security;

namespace Paqueteria.UnitTests.Security;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class SyntheticEnvironmentPolicyTests
{
    [Theory]
    [InlineData("DevSynthetic", "DEV_SYNTHETIC", true)]
    [InlineData("DevSynthetic", null, false)]
    [InlineData("DevSynthetic", "dev_synthetic", false)]
    [InlineData("DevSynthetic", "PRODUCTION", false)]
    [InlineData("Production", "DEV_SYNTHETIC", false)]
    [InlineData("Staging", "DEV_SYNTHETIC", false)]
    [InlineData("Testing", "DEV_SYNTHETIC", false)]
    [InlineData("Development", "DEV_SYNTHETIC", false)]
    public void DevSynthetic_matrix_is_exact_and_fail_closed(
        string environmentName,
        string? deploymentClass,
        bool expected)
    {
        using var deployment = new ProcessEnvironmentVariable(
            SyntheticEnvironmentPolicy.DeploymentClassVariable,
            deploymentClass);

        Assert.Equal(expected, SyntheticEnvironmentPolicy.IsDevSynthetic(environmentName));
        if (environmentName == SyntheticEnvironmentPolicy.EnvironmentName)
        {
            Assert.False(new TestHostEnvironment(environmentName).IsDevelopment());
            Assert.False(new TestHostEnvironment(environmentName).IsEnvironment("Testing"));
        }
    }

    [Fact]
    public void Configuration_cannot_supply_deployment_class_authority()
    {
        using var deployment = new ProcessEnvironmentVariable(
            SyntheticEnvironmentPolicy.DeploymentClassVariable,
            null);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [SyntheticEnvironmentPolicy.DeploymentClassVariable] =
                    SyntheticEnvironmentPolicy.DeploymentClass,
                ["Authentication:Provider"] = "Mock",
            })
            .Build();
        using var provider = new ServiceCollection()
            .AddIdentitySecurity(configuration, new TestHostEnvironment("DevSynthetic"))
            .BuildServiceProvider();

        Assert.False(SyntheticEnvironmentPolicy.IsDevSynthetic("DevSynthetic"));
        Assert.Throws<OptionsValidationException>(() =>
            _ = provider.GetRequiredService<IOptions<IdentityAuthenticationOptions>>().Value);
    }

    [Fact]
    public void Process_deployment_class_authorizes_only_the_approved_mock_providers()
    {
        using var deployment = new ProcessEnvironmentVariable(
            SyntheticEnvironmentPolicy.DeploymentClassVariable,
            SyntheticEnvironmentPolicy.DeploymentClass);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Provider"] = "Mock",
                ["Locations:GeocodingProvider"] = "Mock",
                ["Locations:PiiProtector"] = "Mock",
            })
            .Build();
        var environment = new TestHostEnvironment("DevSynthetic");

        using var identity = new ServiceCollection()
            .AddIdentitySecurity(configuration, environment)
            .BuildServiceProvider();
        Assert.Equal(
            IdentityProviderKind.Mock,
            identity.GetRequiredService<IOptions<IdentityAuthenticationOptions>>().Value.Provider);

        using var locations = new ServiceCollection()
            .AddLocationsInfrastructure(configuration, environment)
            .BuildServiceProvider();
        var options = locations.GetRequiredService<IOptions<LocationsOptions>>().Value;
        Assert.Equal(GeocodingProviderKind.Mock, options.GeocodingProvider);
        Assert.Equal(LocationPiiProtectorKind.Mock, options.PiiProtector);
    }

    [Fact]
    public async Task Authorized_DevSynthetic_readiness_is_healthy_while_operations_remain_disabled()
    {
        using var deployment = new ProcessEnvironmentVariable(
            SyntheticEnvironmentPolicy.DeploymentClassVariable,
            SyntheticEnvironmentPolicy.DeploymentClass);
        var storage = new DisabledProofObjectStorage();
        var scanner = new DisabledProofThreatScanner();
        var environment = new TestHostEnvironment("DevSynthetic");

        var storageHealth = await new ProofStorageHealthCheck(storage, environment)
            .CheckHealthAsync(new HealthCheckContext());
        var scannerHealth = await new ProofScannerHealthCheck(scanner, storage, environment)
            .CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, storageHealth.Status);
        Assert.Equal(HealthStatus.Healthy, scannerHealth.Status);
        await Assert.ThrowsAsync<ProofStorageUnavailableException>(
            () => storage.DeleteQuarantineAsync("quarantine/synthetic", null, default));
        var scan = await scanner.ScanAsync(Stream.Null, default);
        Assert.False(scan.IsSafe);
        Assert.Equal("SCANNER_UNAVAILABLE", scan.Code);
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = nameof(SyntheticEnvironmentPolicyTests);
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
    public const string Name = "Process environment";
}

internal sealed class ProcessEnvironmentVariable : IDisposable
{
    private readonly string name;
    private readonly string? originalValue;

    public ProcessEnvironmentVariable(string name, string? value)
    {
        this.name = name;
        originalValue = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose() => Environment.SetEnvironmentVariable(name, originalValue);
}
