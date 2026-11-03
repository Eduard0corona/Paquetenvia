using Drivers.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Paqueteria.UnitTests.Security;

namespace Paqueteria.UnitTests.Drivers;

/// <summary>
/// DRV-DOC-001-IN-PERSON-2026-11-02 (Y2 "Ya, apagado"): Drivers:Documents is off by default; turning it on where people
/// are real needs the GATE-007 resolution row, always under PAQUETERIA_DEPLOYMENT_CLASS=PILOT_REAL_PEOPLE; and it needs
/// the PostgreSQL provider, recordable required types (Y11) and no non-expiring type. No failure echoes a value.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class DriverDocumentsOptionsTests
{
    private const string Gate = "GATE-007-DRIVER-DOCUMENTS-RESOLVED";

    [Fact]
    public void Documents_are_off_by_default_and_a_disabled_host_needs_nothing()
    {
        using var deploymentClass = new ProcessEnvironmentVariable("PAQUETERIA_DEPLOYMENT_CLASS", "PILOT_REAL_PEOPLE");
        using var provider = Build("Production", []);
        var options = provider.GetRequiredService<IOptions<DriversOptions>>().Value;
        Assert.False(options.Documents.Enabled);
        Assert.Equal(string.Empty, options.Documents.Gate007DecisionId);
    }

    [Theory]
    [InlineData("Production", null)]
    [InlineData("Staging", null)]
    [InlineData("Production", "PILOT_REAL_PEOPLE")]
    [InlineData("Development", "PILOT_REAL_PEOPLE")]
    [InlineData("Testing", "PILOT_REAL_PEOPLE")]
    public void Enabling_documents_where_people_are_real_requires_the_GATE_007_row(string environment, string? deployment)
    {
        using var deploymentClass = new ProcessEnvironmentVariable("PAQUETERIA_DEPLOYMENT_CLASS", deployment);
        var failure = Fails(environment, Enabled());
        Assert.Contains("Drivers:Documents:Gate007DecisionId", failure, StringComparison.Ordinal);
        Assert.Contains("PILOT_REAL_PEOPLE", failure, StringComparison.Ordinal);

        foreach (var malformed in new[] { "PENDING", "GATE-007-x", "gate-007-resolved", "GATE-004-RESOLVED" })
        {
            var message = Fails(environment, Enabled(malformed));
            Assert.DoesNotContain(malformed, message, StringComparison.Ordinal);
        }

        using var provider = Build(environment, Enabled(Gate));
        Assert.True(provider.GetRequiredService<IOptions<DriversOptions>>().Value.Documents.Enabled);
    }

    [Theory]
    [InlineData("Development", null)]
    [InlineData("Testing", null)]
    [InlineData("DevSynthetic", null)]
    [InlineData("Production", "DEV_SYNTHETIC")]
    public void Synthetic_environments_enable_documents_without_the_gate(string environment, string? deployment)
    {
        using var deploymentClass = new ProcessEnvironmentVariable("PAQUETERIA_DEPLOYMENT_CLASS", deployment);
        using var provider = Build(environment, Enabled());
        Assert.True(provider.GetRequiredService<IOptions<DriversOptions>>().Value.Documents.Enabled);
    }

    [Fact]
    public void A_host_without_a_known_environment_is_treated_as_real()
    {
        using var deploymentClass = new ProcessEnvironmentVariable("PAQUETERIA_DEPLOYMENT_CLASS", null);
        using var provider = Build(null, Enabled());
        var failure = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<DriversOptions>>().Value);
        Assert.Contains("Drivers:Documents:Gate007DecisionId", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_malformed_gate_id_is_refused_even_while_documents_are_off()
    {
        using var deploymentClass = new ProcessEnvironmentVariable("PAQUETERIA_DEPLOYMENT_CLASS", null);
        var message = Fails("Testing", new Dictionary<string, string?> { ["Drivers:Documents:Gate007DecisionId"] = "SECRET-ish" });
        Assert.Contains("Drivers:Documents:Gate007DecisionId must name", message, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-ish", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Enabled_documents_need_the_store_recordable_types_and_expiring_documents()
    {
        using var deploymentClass = new ProcessEnvironmentVariable("PAQUETERIA_DEPLOYMENT_CLASS", null);
        var withoutStore = Enabled();
        withoutStore["Drivers:Provider"] = "Disabled";
        Assert.Contains("requires Drivers:Provider=PostgreSql", Fails("Testing", withoutStore), StringComparison.Ordinal);

        var background = Enabled();
        background["Drivers:Eligibility:RequiredDocumentTypesByVehicleType:CAR:4"] = "BACKGROUND_CHECK";
        Assert.Contains("IDENTITY, DRIVER_LICENSE, VEHICLE_CARD or INSURANCE", Fails("Testing", background), StringComparison.Ordinal);

        var nonExpiring = Enabled();
        nonExpiring["Drivers:Eligibility:NonExpiringDocumentTypes:0"] = "IDENTITY";
        Assert.Contains("NonExpiringDocumentTypes to stay empty", Fails("Testing", nonExpiring), StringComparison.Ordinal);
    }

    private static Dictionary<string, string?> Enabled(string? gate = null)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Drivers:Provider"] = "PostgreSql",
            ["ConnectionStrings:Paqueteria"] = "Host=localhost;Database=unit;Username=unit;Password=unit",
            ["Drivers:Documents:Enabled"] = "true",
        };
        foreach (var vehicle in new[] { "MOTORCYCLE", "CAR", "VAN" })
        {
            var types = new[] { "IDENTITY", "DRIVER_LICENSE", "VEHICLE_CARD", "INSURANCE" };
            for (var index = 0; index < types.Length; index++)
            {
                settings[$"Drivers:Eligibility:RequiredDocumentTypesByVehicleType:{vehicle}:{index}"] = types[index];
            }
        }

        settings["Drivers:Eligibility:RequiredDocumentTypesByVehicleType:BICYCLE:0"] = "IDENTITY";
        settings["Drivers:Eligibility:RequiredDocumentTypesByVehicleType:WALKER:0"] = "IDENTITY";
        foreach (var vehicle in new[] { "MOTORCYCLE", "CAR", "VAN", "BICYCLE", "WALKER" })
        {
            settings[$"Drivers:Eligibility:VehicleCapacity:{vehicle}:MaximumPackageCount"] = "5";
            settings[$"Drivers:Eligibility:VehicleCapacity:{vehicle}:MaximumTotalWeightGrams"] = "20000";
            settings[$"Drivers:Eligibility:VehicleCapacity:{vehicle}:MaximumSinglePackageWeightGrams"] = "10000";
            settings[$"Drivers:Eligibility:VehicleCapacity:{vehicle}:MaximumLengthMillimeters"] = "400";
            settings[$"Drivers:Eligibility:VehicleCapacity:{vehicle}:MaximumWidthMillimeters"] = "400";
            settings[$"Drivers:Eligibility:VehicleCapacity:{vehicle}:MaximumHeightMillimeters"] = "350";
        }

        if (gate is not null)
        {
            settings["Drivers:Documents:Gate007DecisionId"] = gate;
        }

        return settings;
    }

    private static string Fails(string? environment, Dictionary<string, string?> settings)
    {
        using var provider = Build(environment, settings);
        var failure = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<DriversOptions>>().Value);
        return failure.Message + " " + string.Join(' ', failure.Failures);
    }

    private static ServiceProvider Build(string? environment, Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        if (environment is not null)
        {
            services.AddSingleton<IHostEnvironment>(new UnitHostEnvironment(environment));
        }

        services.AddDriversInfrastructure(configuration);
        return services.BuildServiceProvider();
    }

    private sealed class UnitHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = nameof(DriverDocumentsOptionsTests);
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
