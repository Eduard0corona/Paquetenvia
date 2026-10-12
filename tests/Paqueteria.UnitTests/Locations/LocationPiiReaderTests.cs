using Locations.Application.Geocoding;
using Locations.Application.Locations;
using Locations.Infrastructure;
using Locations.Infrastructure.Geocoding;
using Locations.Infrastructure.Locations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Paqueteria.Application.Locations;
using Paqueteria.Infrastructure.Security.Pii;
using Paqueteria.UnitTests.Security;

namespace Paqueteria.UnitTests.Locations;

/// <summary>
/// UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11: the Locations port that reads back the protected address and contact name
/// of stored locations, with the mode, purposes and storage format the Locations module protected them with.
/// </summary>
public sealed class LocationPiiReaderTests
{
    private const string PiiKeyId = "https://paqueteria-test.vault.azure.net/keys/pii-envelope";
    private static readonly Guid Tenant = Guid.Parse("10000000-0000-4000-8000-000000000001");

    [Fact]
    public async Task The_envelope_reader_opens_what_the_protector_sealed_and_splits_the_references()
    {
        var envelope = new PiiEnvelopeProtector(new FakePiiKeyVault());
        var withReferences = await SealAsync(
            envelope, Guid.NewGuid(), LocationAddressText.Compose("Calle Sintética 1", "Portón azul"), "María López Pérez");
        var withoutContact = await SealAsync(envelope, Guid.NewGuid(), "Calle Sintética 2", null);

        var texts = await new EnvelopeLocationPiiReader(envelope).ReadAsync([withReferences, withoutContact], default);

        Assert.Equal(
            [
                new LocationPiiText("Calle Sintética 1", "Portón azul", "María López Pérez"),
                new LocationPiiText("Calle Sintética 2", null, null),
            ],
            texts);
        Assert.DoesNotContain("Calle", texts[0]!.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("María", texts[0]!.ToString(), StringComparison.Ordinal);
        Assert.Contains("[ciphertexts redacted]", withReferences.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_value_rejected_for_its_row_leaves_only_that_location_unreadable()
    {
        var envelope = new PiiEnvelopeProtector(new FakePiiKeyVault());
        var good = await SealAsync(envelope, Guid.NewGuid(), "Calle Sintética 1", "Ana Ruiz");
        var sealedValue = await SealAsync(envelope, Guid.NewGuid(), "Calle Sintética 2", "Luis Mora");
        var locations = new[]
        {
            good,
            sealedValue with { OwnerOrganizationId = Guid.NewGuid() },
            sealedValue with { LocationId = Guid.NewGuid() },
            sealedValue with { AddressCiphertext = sealedValue.ContactNameCiphertext! },
            sealedValue with { AddressCiphertext = [1, 2, 3] },
            sealedValue with { ContactNameCiphertext = [0] },
            sealedValue,
        };

        var texts = await new EnvelopeLocationPiiReader(envelope).ReadAsync(locations, default);

        Assert.Equal(new LocationPiiText("Calle Sintética 1", null, "Ana Ruiz"), texts[0]);
        Assert.All(texts.Skip(1).Take(5), Assert.Null);
        Assert.Equal(new LocationPiiText("Calle Sintética 2", null, "Luis Mora"), texts[6]);
    }

    [Fact]
    public async Task An_unavailable_key_service_stops_the_batch_without_throwing()
    {
        var vault = new FakePiiKeyVault();
        var envelope = new PiiEnvelopeProtector(vault);
        var locations = new List<StoredLocationPii>();
        for (var index = 0; index < 20; index++)
        {
            locations.Add(await SealAsync(envelope, Guid.NewGuid(), $"Calle Sintética {index}", null));
        }

        vault.FailOn = FakePiiKeyVault.Failure.Unwrap;
        var texts = await new EnvelopeLocationPiiReader(envelope).ReadAsync(locations, default);

        Assert.Equal(20, texts.Count);
        Assert.All(texts, Assert.Null);
    }

    [Fact]
    public async Task Cancellation_by_the_caller_is_not_swallowed()
    {
        var envelope = new PiiEnvelopeProtector(new FakePiiKeyVault());
        var location = await SealAsync(envelope, Guid.NewGuid(), "Calle Sintética 1", null);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new EnvelopeLocationPiiReader(envelope).ReadAsync([location], cancelled.Token));
    }

    [Fact]
    public async Task The_synthetic_reader_never_decrypts_and_the_disabled_one_reads_nothing()
    {
        var digest = new DeterministicMockLocationPiiProtector().Protect("Calle Real 1", DeterministicMockLocationPiiProtector.KeyVersion);
        var locations = new[]
        {
            new StoredLocationPii(Tenant, Guid.NewGuid(), DeterministicMockLocationPiiProtector.KeyVersion, digest, digest),
            new StoredLocationPii(Tenant, Guid.NewGuid(), DeterministicMockLocationPiiProtector.KeyVersion, digest, null),
        };

        Assert.Equal(
            [
                new LocationPiiText(SyntheticLocationPiiReader.AddressPlaceholder, null, SyntheticLocationPiiReader.ContactNamePlaceholder),
                new LocationPiiText(SyntheticLocationPiiReader.AddressPlaceholder, null, null),
            ],
            await new SyntheticLocationPiiReader().ReadAsync(locations, default));
        Assert.Equal([null, null], await new DisabledLocationPiiReader().ReadAsync(locations, default));
    }

    [Fact]
    public void The_storage_format_round_trips_and_is_the_one_the_location_service_writes()
    {
        Assert.Equal("Calle 1", LocationAddressText.Compose("Calle 1", null));
        Assert.Equal("Calle 1", LocationAddressText.Compose("Calle 1", "  "));
        Assert.Equal("Calle 1\nReferences: Portón", LocationAddressText.Compose("Calle 1", "Portón"));
        Assert.Equal(("Calle 1", "Portón\nverde"), LocationAddressText.Split("Calle 1\nReferences: Portón\nverde"));
        Assert.Equal(("Calle 1", (string?)null), LocationAddressText.Split("Calle 1"));

        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Modules", "Locations", "Locations.Infrastructure", "Locations",
            "PostgreSqlLocationService.cs"));
        Assert.Contains("LocationAddressText.Compose(command.AddressText, command.References)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("\\nReferences: ", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Mock", "Testing", typeof(SyntheticLocationPiiReader))]
    [InlineData("mock", "Development", typeof(SyntheticLocationPiiReader))]
    [InlineData("1", "Testing", typeof(SyntheticLocationPiiReader))]
    [InlineData("AzureKeyVault", "Production", typeof(EnvelopeLocationPiiReader))]
    [InlineData("azurekeyvault", "Production", typeof(EnvelopeLocationPiiReader))]
    [InlineData("2", "Production", typeof(EnvelopeLocationPiiReader))]
    [InlineData("Disabled", "Production", typeof(DisabledLocationPiiReader))]
    [InlineData(null, "Production", typeof(DisabledLocationPiiReader))]
    public void The_reader_follows_the_bound_protector_option_exactly_as_the_protector_does(
        string? setting,
        string environment,
        Type expectedReader)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Locations:PiiProtector"] = setting,
            ["PiiProtection:AzureKeyVault:KeyId"] = PiiKeyId,
            ["ConnectionStrings:Paqueteria"] = "Host=localhost;Database=not-used;Username=not-used;Password=not-used",
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IPiiKeyWrapClient>(new FakePiiKeyVault());
        services.AddLocationsInfrastructure(configuration, new FixedEnvironment(environment));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var reader = scope.ServiceProvider.GetRequiredService<ILocationPiiReader>();
        var protector = scope.ServiceProvider.GetRequiredService<ILocationPiiProtector>();
        Assert.IsType(expectedReader, reader);
        Assert.IsType(
            expectedReader == typeof(SyntheticLocationPiiReader)
                ? typeof(DeterministicMockLocationPiiProtector)
                : expectedReader == typeof(EnvelopeLocationPiiReader)
                    ? typeof(AzureKeyVaultLocationPiiProtector)
                    : typeof(DisabledLocationPiiProtector),
            protector);
    }

    private static async Task<StoredLocationPii> SealAsync(
        IPiiEnvelopeProtector envelope,
        Guid locationId,
        string addressText,
        string? contactName)
    {
        var sealedValues = await new AzureKeyVaultLocationPiiProtector(envelope).ProtectAsync(
            new LocationPiiValues(Tenant, locationId, addressText, contactName, null),
            default);
        return new StoredLocationPii(
            Tenant,
            locationId,
            sealedValues.KeyVersion,
            sealedValues.AddressTextCiphertext,
            sealedValues.ContactNameCiphertext);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Paqueteria.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private sealed class FixedEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "LocationPiiReaderTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
