using System.Text.Json;
using Paqueteria.ArchitectureTests.Architecture;

namespace Paqueteria.ArchitectureTests;

/// <summary>
/// SCL-001 ships exactly one versioned profile that enables the shared PostgreSQL key ring, and
/// that profile is a validation profile rather than production authorization.
/// </summary>
public sealed class Scl001ScaleReadyProfileArchitectureTests
{
    private const string Profile = "appsettings.ScaleReady.json";
    private const string Documentation = "docs/development/scl-001-stateless-distributed-hosts.md";

    [Theory]
    [InlineData("src/Paqueteria.Api", "Paqueteria", "paqueteria_app")]
    [InlineData("src/Paqueteria.Worker", "PaqueteriaWorker", "paqueteria_worker")]
    public void Scale_ready_profile_is_versioned_and_enables_the_shared_key_ring(
        string host,
        string connectionStringName,
        string runtimeRole)
    {
        var profile = ReadDataProtection($"{host}/{Profile}");

        Assert.Equal("PostgreSql", profile.GetProperty("Provider").GetString());
        Assert.Equal("Paquetenvia", profile.GetProperty("ApplicationName").GetString());
        Assert.Equal(connectionStringName, profile.GetProperty("ConnectionStringName").GetString());
        Assert.Equal(runtimeRole, profile.GetProperty("RuntimeRole").GetString());
    }

    [Theory]
    [InlineData("src/Paqueteria.Api")]
    [InlineData("src/Paqueteria.Worker")]
    public void Default_configuration_keeps_single_instance_behaviour(string host)
    {
        Assert.Equal("Disabled", ReadDataProtection($"{host}/appsettings.json")
            .GetProperty("Provider").GetString());

        // Nothing but the explicit profile may turn the distributed mode on.
        var enabling = Directory
            .GetFiles(TestRepository.GetPath(host), "appsettings*.json")
            .Where(path => File.ReadAllText(path).Contains("\"PostgreSql\"", StringComparison.Ordinal))
            .Select(path => Path.GetFileName(path)!)
            .ToArray();

        Assert.Equal<string>([Profile], enabling);
    }

    [Fact]
    public void Documentation_records_the_external_key_encryption_requirement()
    {
        var documentation = File.ReadAllText(TestRepository.GetPath(Documentation));

        Assert.Contains(
            "An external key-encryption protector is required before productive distributed Data Protection activation.",
            documentation,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Scale_ready_profile_provisions_no_cloud_resources_or_secrets()
    {
        foreach (var host in new[] { "src/Paqueteria.Api", "src/Paqueteria.Worker" })
        {
            var profile = File.ReadAllText(TestRepository.GetPath($"{host}/{Profile}"));
            foreach (var forbidden in new[] { "KeyVault", "vault.azure.net", "Certificate", "Password" })
            {
                Assert.DoesNotContain(forbidden, profile, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private static JsonElement ReadDataProtection(string relativePath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(TestRepository.GetPath(relativePath)));
        return document.RootElement.GetProperty("DataProtection").Clone();
    }
}
