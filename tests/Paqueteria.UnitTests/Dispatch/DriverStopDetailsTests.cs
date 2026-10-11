using Dispatch.Application.Stops;
using Dispatch.Domain;
using Dispatch.Infrastructure;
using Dispatch.Infrastructure.Stops;
using Locations.Application.Geocoding;
using Locations.Infrastructure.Geocoding;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Paqueteria.Infrastructure.Security.Pii;
using Paqueteria.UnitTests.Security;

namespace Paqueteria.UnitTests.Dispatch;

/// <summary>
/// UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11: the short contact name rule, the address/references split and the
/// readers that turn the protected location values into the driver's stop details.
/// </summary>
public sealed class DriverStopDetailsTests
{
    private static readonly Guid Tenant = Guid.Parse("10000000-0000-4000-8000-000000000001");
    private static readonly Guid LocationId = Guid.Parse("20000000-0000-4000-8000-000000000002");

    [Theory]
    [InlineData("María López", "M. López")]
    [InlineData("María López Pérez", "M. Pérez")]
    [InlineData("  maría   del   carmen  lópez ", "M. lópez")]
    [InlineData("Ana María Ruiz", "A. Ruiz")]
    [InlineData("José-Luis Ñúñez", "J. Ñúñez")]
    [InlineData("álvaro\tGarcía", "Á. García")]
    [InlineData("María", "María")]
    [InlineData("Ñ", "Ñ")]
    public void The_short_name_is_the_first_initial_and_the_last_word(string contact, string expected)
    {
        Assert.Equal(expected, DriverStopContactName.Shorten(contact));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_contact_name_has_no_short_name(string? contact)
    {
        Assert.Null(DriverStopContactName.Shorten(contact));
        Assert.Null(DriverStopContactName.Full(contact));
        foreach (var stopType in Enum.GetValues<DriverStopType>())
        {
            Assert.Equal(new DriverStopContact(null, null), DriverStopContactName.ForStop(stopType, contact));
        }
    }

    [Fact]
    public void A_pickup_shows_the_full_name_and_the_other_stops_only_the_short_one()
    {
        // The owner's answer "Completo en recolección": the full name on a pickup, the short name at the door.
        Assert.Equal(
            new DriverStopContact("Farmacia Las Quintas", null),
            DriverStopContactName.ForStop(DriverStopType.Pickup, "  Farmacia \t Las\nQuintas "));
        Assert.Equal(
            new DriverStopContact(null, "M. Pérez"),
            DriverStopContactName.ForStop(DriverStopType.Delivery, "María López Pérez"));
        Assert.Equal(
            new DriverStopContact(null, "M. Pérez"),
            DriverStopContactName.ForStop(DriverStopType.Return, "María López Pérez"));
        Assert.DoesNotContain("Farmacia", new DriverStopContact("Farmacia Las Quintas", null).ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_full_name_is_cut_at_its_bound_with_an_ellipsis()
    {
        var full = DriverStopContactName.Full("Farmacia " + new string('q', 400));
        Assert.Equal(DriverStopContactName.MaximumFullLength, full!.Length);
        Assert.StartsWith("Farmacia qq", full, StringComparison.Ordinal);
        Assert.EndsWith("…", full, StringComparison.Ordinal);
    }

    [Fact]
    public void The_short_name_stays_within_its_bound_on_a_text_element_boundary()
    {
        var shortName = DriverStopContactName.Shorten("Ana " + string.Concat(Enumerable.Repeat("👩‍💻", 40)));
        Assert.NotNull(shortName);
        Assert.True(shortName!.Length <= DriverStopContactName.MaximumLength);
        Assert.StartsWith("A. 👩‍💻", shortName, StringComparison.Ordinal);
        Assert.Equal(0, (shortName.Length - 3) % "👩‍💻".Length);
        Assert.True(DriverStopContactName.Shorten(new string('x', 200))!.Length <= DriverStopContactName.MaximumLength);
    }

    [Fact]
    public void The_address_and_its_references_are_split_back_as_Locations_stored_them()
    {
        Assert.True(DriverStopAddressText.TrySplit(
            "Calle Sintética 100, Tres Ríos\nReferences: Portón azul\nfrente al parque",
            out var address,
            out var references));
        Assert.Equal("Calle Sintética 100, Tres Ríos", address);
        Assert.Equal("Portón azul\nfrente al parque", references);

        Assert.True(DriverStopAddressText.TrySplit(" Calle Sintética 200 ", out address, out references));
        Assert.Equal("Calle Sintética 200", address);
        Assert.Null(references);

        Assert.True(DriverStopAddressText.TrySplit("Calle Sintética 300\nReferences:  ", out address, out references));
        Assert.Equal("Calle Sintética 300", address);
        Assert.Null(references);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\nReferences: solo referencias")]
    public void A_text_without_address_fails_closed(string? text)
    {
        Assert.False(DriverStopAddressText.TrySplit(text, out _, out _));
    }

    [Fact]
    public void Long_texts_are_cut_at_their_bounds_with_an_ellipsis()
    {
        Assert.True(DriverStopAddressText.TrySplit(
            new string('a', 1500) + DriverStopAddressText.ReferencesSeparator + new string('r', 900),
            out var address,
            out var references));
        Assert.Equal(DriverStopAddressText.MaximumAddressLength, address.Length);
        Assert.EndsWith("…", address, StringComparison.Ordinal);
        Assert.Equal(DriverStopAddressText.MaximumReferencesLength, references!.Length);
        Assert.EndsWith("…", references, StringComparison.Ordinal);
    }

    [Fact]
    public void The_separator_is_the_one_Locations_writes()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Modules", "Locations", "Locations.Infrastructure", "Locations",
            "PostgreSqlLocationService.cs"));
        Assert.Contains("$\"{command.AddressText}\\nReferences: {command.References}\"", source, StringComparison.Ordinal);
        Assert.Equal("\nReferences: ", DriverStopAddressText.ReferencesSeparator);
    }

    [Fact]
    public void The_envelope_purposes_are_the_Locations_columns()
    {
        Assert.Equal(AzureKeyVaultLocationPiiProtector.AddressTextPurpose, EnvelopeDriverStopLocationReader.AddressTextPurpose);
        Assert.Equal(AzureKeyVaultLocationPiiProtector.ContactNamePurpose, EnvelopeDriverStopLocationReader.ContactNamePurpose);
    }

    [Fact]
    public async Task The_envelope_reader_decrypts_what_the_Locations_protector_sealed()
    {
        var vault = new FakePiiKeyVault();
        var envelope = new PiiEnvelopeProtector(vault);
        var sealedWithContact = await new AzureKeyVaultLocationPiiProtector(envelope).ProtectAsync(
            new LocationPiiValues(Tenant, LocationId, "Calle Sintética 1\nReferences: Portón", "María López Pérez", "6670000000"),
            default);
        var otherLocation = Guid.NewGuid();
        var sealedWithoutContact = await new AzureKeyVaultLocationPiiProtector(envelope).ProtectAsync(
            new LocationPiiValues(Tenant, otherLocation, "Calle Sintética 2", null, null),
            default);

        var texts = await new EnvelopeDriverStopLocationReader(envelope).ReadAsync(
            [
                new ProtectedDriverStopLocation(
                    Tenant,
                    LocationId,
                    sealedWithContact.KeyVersion,
                    sealedWithContact.AddressTextCiphertext,
                    sealedWithContact.ContactNameCiphertext),
                new ProtectedDriverStopLocation(
                    Tenant,
                    otherLocation,
                    sealedWithoutContact.KeyVersion,
                    sealedWithoutContact.AddressTextCiphertext,
                    null),
            ],
            default);

        Assert.Equal(
            [
                new DriverStopLocationText("Calle Sintética 1\nReferences: Portón", "María López Pérez"),
                new DriverStopLocationText("Calle Sintética 2", null),
            ],
            texts);
        Assert.DoesNotContain("Calle", texts[0].ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("María", texts[0].ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_value_bound_to_another_row_or_tenant_fails_the_whole_batch_closed()
    {
        var envelope = new PiiEnvelopeProtector(new FakePiiKeyVault());
        var sealedValue = await new AzureKeyVaultLocationPiiProtector(envelope).ProtectAsync(
            new LocationPiiValues(Tenant, LocationId, "Calle Sintética 1", "María López", null),
            default);
        var reader = new EnvelopeDriverStopLocationReader(envelope);

        foreach (var location in new[]
                 {
                     new ProtectedDriverStopLocation(Guid.NewGuid(), LocationId, sealedValue.KeyVersion, sealedValue.AddressTextCiphertext, null),
                     new ProtectedDriverStopLocation(Tenant, Guid.NewGuid(), sealedValue.KeyVersion, sealedValue.AddressTextCiphertext, null),
                     new ProtectedDriverStopLocation(Tenant, LocationId, sealedValue.KeyVersion, sealedValue.ContactNameCiphertext!, null),
                     new ProtectedDriverStopLocation(Tenant, LocationId, sealedValue.KeyVersion, [1, 2, 3], null),
                 })
        {
            var exception = await Assert.ThrowsAsync<DriverStopsPiiUnavailableException>(() =>
                reader.ReadAsync([location], default));
            Assert.DoesNotContain("Calle", exception.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("María", exception.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task An_unreachable_key_vault_fails_closed()
    {
        var vault = new FakePiiKeyVault();
        var envelope = new PiiEnvelopeProtector(vault);
        var sealedValue = await new AzureKeyVaultLocationPiiProtector(envelope).ProtectAsync(
            new LocationPiiValues(Tenant, LocationId, "Calle Sintética 1", null, null),
            default);
        vault.FailOn = FakePiiKeyVault.Failure.Unwrap;

        await Assert.ThrowsAsync<DriverStopsPiiUnavailableException>(() =>
            new EnvelopeDriverStopLocationReader(envelope).ReadAsync(
                [new ProtectedDriverStopLocation(Tenant, LocationId, sealedValue.KeyVersion, sealedValue.AddressTextCiphertext, null)],
                default));
    }

    [Fact]
    public async Task The_synthetic_reader_never_decrypts_and_the_disabled_one_fails_closed()
    {
        var digest = new DeterministicMockLocationPiiProtector().Protect("Calle Real 1", DeterministicMockLocationPiiProtector.KeyVersion);
        var texts = await new SyntheticDriverStopLocationReader().ReadAsync(
            [
                new ProtectedDriverStopLocation(Tenant, LocationId, DeterministicMockLocationPiiProtector.KeyVersion, digest, digest),
                new ProtectedDriverStopLocation(Tenant, LocationId, DeterministicMockLocationPiiProtector.KeyVersion, digest, null),
            ],
            default);

        Assert.Equal(
            [
                new DriverStopLocationText(SyntheticDriverStopLocationReader.AddressPlaceholder, SyntheticDriverStopLocationReader.ContactNamePlaceholder),
                new DriverStopLocationText(SyntheticDriverStopLocationReader.AddressPlaceholder, null),
            ],
            texts);
        await Assert.ThrowsAsync<DriverStopsPiiUnavailableException>(() =>
            new DisabledDriverStopLocationReader().ReadAsync(
                [new ProtectedDriverStopLocation(Tenant, LocationId, DeterministicMockLocationPiiProtector.KeyVersion, digest, null)],
                default));
    }

    [Theory]
    [InlineData("AzureKeyVault", DriverStopLocationProtection.Envelope)]
    [InlineData("azurekeyvault", DriverStopLocationProtection.Envelope)]
    [InlineData("Mock", DriverStopLocationProtection.Synthetic)]
    [InlineData("Disabled", DriverStopLocationProtection.Disabled)]
    [InlineData("", DriverStopLocationProtection.Disabled)]
    [InlineData(null, DriverStopLocationProtection.Disabled)]
    [InlineData("Plaintext", DriverStopLocationProtection.Disabled)]
    public void Stop_details_are_read_back_the_way_Locations_protected_them(
        string? setting,
        DriverStopLocationProtection expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Locations:PiiProtector"] = setting })
            .Build();
        Assert.Equal(expected, DriverStopLocationProtectionSetting.Resolve(configuration));
    }

    [Theory]
    [InlineData("Mock", typeof(SyntheticDriverStopLocationReader))]
    [InlineData("Disabled", typeof(DisabledDriverStopLocationReader))]
    public void The_dispatch_composition_selects_the_reader_from_the_Locations_setting(string setting, Type expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Locations:PiiProtector"] = setting,
                ["Dispatch:Provider"] = "Disabled",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddDispatchInfrastructure(configuration);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.IsType(expected, scope.ServiceProvider.GetRequiredService<IDriverStopLocationReader>());
    }

    [Fact]
    public void Results_never_print_the_stop_details()
    {
        var stop = new DriverStopResult(
            Guid.NewGuid(),
            3,
            "ORD_SYNTHETIC",
            "DELIVERY",
            "DELIVERING",
            "Resumen",
            "M. López",
            "Farmacia Las Quintas",
            "Tres Ríos",
            null,
            new DriverStopCodResult(125_000, false),
            new DriverStopNavigationResult(24.8, -107.39, "Calle Sintética 1", "Portón"));
        var text = stop.ToString() + stop.Navigation;
        foreach (var value in new[] { "M. López", "Farmacia", "Tres Ríos", "Calle", "Portón", "24.8", "125000" })
        {
            Assert.DoesNotContain(value, text, StringComparison.Ordinal);
        }
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
}
