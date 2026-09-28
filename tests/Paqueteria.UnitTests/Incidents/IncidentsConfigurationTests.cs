using System.Text;
using Incidents.Application.Incidents;
using Incidents.Domain;
using Incidents.Infrastructure;
using Incidents.Infrastructure.Incidents;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Paqueteria.UnitTests.Incidents;

/// <summary>
/// The bounded operational surface of INC-001 as a deployment sees it: the approved MVP-1
/// defaults, the validation that refuses anything outside them, and the description protector,
/// which is disabled until a deployment says otherwise and never degrades to plaintext.
/// </summary>
public sealed class IncidentsConfigurationTests
{
    [Fact]
    public void The_unconfigured_module_binds_the_approved_MVP1_defaults()
    {
        var options = Resolve([]);

        Assert.True(options.OperationalPolicy.IsValid);
        Assert.Equal(IncidentOperationalPolicy.Mvp1, options.OperationalPolicy);
        Assert.Equal(2, options.CriticalSlaHours);
        Assert.Equal(8, options.HighSlaHours);
        Assert.Equal(24, options.MediumSlaHours);
        Assert.Equal(72, options.LowSlaHours);
        Assert.Equal(72, options.MaximumOccurrenceAgeHours);
        Assert.Equal(5, options.MaximumOccurrenceSkewMinutes);
        Assert.Equal(IncidentEvidencePolicy.MaximumEvidenceCount, options.MaximumEvidenceCount);
    }

    [Fact]
    public async Task An_unconfigured_deployment_protects_nothing_and_therefore_persists_nothing()
    {
        var options = Resolve([]);
        Assert.Equal(IncidentPiiProtectorKind.Disabled, options.PiiProtector);

        using var provider = Build([]);
        var protector = provider.GetRequiredService<IIncidentPiiProtector>();
        Assert.IsType<DisabledIncidentPiiProtector>(protector);
        await Assert.ThrowsAsync<IncidentPiiProtectionUnavailableException>(
            () => protector.ProtectAsync(new IncidentPiiBinding(Guid.NewGuid(), Guid.NewGuid()), "una descripcion", CancellationToken.None));
    }

    [Fact]
    public void The_synthetic_protector_is_deterministic_and_never_returns_the_plaintext()
    {
        const string plaintext = "El destinatario no se encontraba en el domicilio.";
        var protector = new DeterministicMockIncidentPiiProtector();

        var first = protector.Protect(plaintext, "inc001-v1");
        var second = protector.Protect(plaintext, "inc001-v1");

        // A replay reproduces the same stored bytes.
        Assert.Equal(first, second);
        Assert.NotEmpty(first);
        Assert.NotEqual(Encoding.UTF8.GetBytes(plaintext), first);
        // A different key version is a different ciphertext.
        Assert.NotEqual(first, protector.Protect(plaintext, "inc001-v2"));
    }

    [Fact]
    public void The_synthetic_protector_is_refused_outside_a_synthetic_environment()
    {
        var settings = new Dictionary<string, string?> { ["Incidents:PiiProtector"] = "Mock" };

        Assert.Equal(
            IncidentPiiProtectorKind.Mock,
            Resolve(settings, environmentName: "Development").PiiProtector);
        var failure = Assert.Throws<OptionsValidationException>(
            () => Resolve(settings, environmentName: "Production"));
        Assert.Contains("DEV_SYNTHETIC_ONLY", string.Join(' ', failure.Failures), StringComparison.Ordinal);
    }

    [Theory]
    // A severer incident may never be given a laxer deadline.
    [InlineData("Incidents:CriticalSlaHours", "9")]
    [InlineData("Incidents:LowSlaHours", "1")]
    // Windows are positive and bounded.
    [InlineData("Incidents:MediumSlaHours", "0")]
    [InlineData("Incidents:HighSlaHours", "-8")]
    [InlineData("Incidents:MaximumOccurrenceAgeHours", "0")]
    [InlineData("Incidents:MaximumOccurrenceAgeHours", "9000")]
    // The retrospective window is capped at the 72-hour idempotency-key floor.
    [InlineData("Incidents:MaximumOccurrenceAgeHours", "73")]
    [InlineData("Incidents:MaximumOccurrenceAgeHours", "96")]
    [InlineData("Incidents:MaximumOccurrenceSkewMinutes", "-1")]
    [InlineData("Incidents:MaximumOccurrenceSkewMinutes", "120")]
    // Evidence may be tightened, never removed and never widened past the published bound.
    [InlineData("Incidents:MaximumEvidenceCount", "0")]
    [InlineData("Incidents:MaximumEvidenceCount", "11")]
    // The key version is recorded beside every ciphertext, so it cannot be blank.
    [InlineData("Incidents:PiiKeyVersion", "")]
    [InlineData("Incidents:CommandTimeoutSeconds", "0")]
    public void An_invalid_operational_configuration_fails_closed(string key, string value)
    {
        Assert.Throws<OptionsValidationException>(
            () => Resolve(new Dictionary<string, string?> { [key] = value }));
    }

    [Fact]
    public void A_tightened_but_coherent_configuration_is_accepted()
    {
        var options = Resolve(new Dictionary<string, string?>
        {
            ["Incidents:MaximumOccurrenceAgeHours"] = "24",
            ["Incidents:MaximumEvidenceCount"] = "3",
        });

        Assert.True(options.OperationalPolicy.IsValid);
        Assert.Equal(TimeSpan.FromHours(24), options.OperationalPolicy.MaximumOccurrenceAge);
        Assert.False(options.OperationalPolicy.IsAllowedEvidenceCount(4));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(24)]
    [InlineData(72)]
    public void An_occurrence_age_from_one_to_72_hours_is_accepted(int hours)
    {
        var options = Resolve(new Dictionary<string, string?>
        {
            ["Incidents:MaximumOccurrenceAgeHours"] = hours.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });

        Assert.True(options.OperationalPolicy.IsValid);
        Assert.Equal(TimeSpan.FromHours(hours), options.OperationalPolicy.MaximumOccurrenceAge);
    }

    [Fact]
    public void An_occurrence_age_above_72_hours_fails_the_start()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Incidents:MaximumOccurrenceAgeHours"] = "73" })
            .Build();
        using var provider = new ServiceCollection()
            .AddIncidentsInfrastructure(configuration, new TestHostEnvironment("Testing"))
            .BuildServiceProvider();

        // ValidateOnStart: the startup validator refuses the value before any request is served.
        var validator = provider.GetRequiredService<IStartupValidator>();
        var failure = Assert.Throws<OptionsValidationException>(() => validator.Validate());
        Assert.Contains("1 to 72 hours", string.Join(' ', failure.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public void The_registered_occurrence_rule_reads_the_configured_age_and_skew()
    {
        using (var defaults = Build([]))
        {
            var policy = defaults.GetRequiredService<IncidentOccurrenceAgePolicy>();
            Assert.Equal(TimeSpan.FromHours(72), policy.MaximumAge);
            Assert.Equal(TimeSpan.FromMinutes(5), policy.ClockTolerance);
        }

        using var configured = Build(new Dictionary<string, string?>
        {
            ["Incidents:MaximumOccurrenceAgeHours"] = "24",
            ["Incidents:MaximumOccurrenceSkewMinutes"] = "0",
        });
        var tightened = configured.GetRequiredService<IncidentOccurrenceAgePolicy>();
        Assert.Equal(TimeSpan.FromHours(24), tightened.MaximumAge);
        Assert.Equal(TimeSpan.Zero, tightened.ClockTolerance);
    }

    private static IncidentsOptions Resolve(
        IEnumerable<KeyValuePair<string, string?>> settings,
        string environmentName = "Testing")
    {
        using var provider = Build(settings, environmentName);
        return provider.GetRequiredService<IOptions<IncidentsOptions>>().Value;
    }

    private static ServiceProvider Build(
        IEnumerable<KeyValuePair<string, string?>> settings,
        string environmentName = "Testing")
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
        return new ServiceCollection()
            .AddIncidentsInfrastructure(configuration, new TestHostEnvironment(environmentName))
            .BuildServiceProvider();
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = nameof(IncidentsConfigurationTests);
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
