using System.Security.Cryptography;
using System.Text;
using Incidents.Application.Incidents;
using Incidents.Infrastructure.Incidents;
using Locations.Application.Geocoding;
using Locations.Infrastructure.Geocoding;
using Microsoft.Extensions.DependencyInjection;
using Paqueteria.Infrastructure.Security.Pii;

namespace Paqueteria.UnitTests.Security;

/// <summary>
/// ADP-001-PII-KEYVAULT-ENVELOPE with a fake key-encryption key (no Azure access): fresh data key
/// per value, server-chosen key version, rotation round trip, column/version binding and fail-closed
/// behaviour.
/// </summary>
public sealed class Adp001PiiEnvelopeTests
{
    private const string Address = "Calle Sintetica 123, Colonia Prueba, Chihuahua";
    private const string Phone = "+52 614 000 0000";
    private const string Contact = "Persona Sintetica";
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherTenant = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid RowId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid LocationId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid IncidentId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly PiiBinding Row = new(Tenant, RowId);
    private static readonly PiiBinding LocationRow = new(Tenant, LocationId);
    private static readonly PiiBinding IncidentRow = new(Tenant, IncidentId);

    [Fact]
    public async Task A_ciphertext_relocated_to_another_tenant_or_row_fails_authentication()
    {
        var protector = new PiiEnvelopeProtector(new FakePiiKeyVault());
        var batch = await protector.ProtectAsync(Row, [new PiiPlaintext("incidents.description", Address)], default);

        Assert.Equal(Address, await protector.UnprotectAsync(Row, "incidents.description", batch.Ciphertexts[0], batch.KeyVersion, default));
        await Assert.ThrowsAsync<PiiCiphertextRejectedException>(() =>
            protector.UnprotectAsync(new PiiBinding(OtherTenant, RowId), "incidents.description", batch.Ciphertexts[0], batch.KeyVersion, default));
        await Assert.ThrowsAsync<PiiCiphertextRejectedException>(() =>
            protector.UnprotectAsync(new PiiBinding(Tenant, Guid.NewGuid()), "incidents.description", batch.Ciphertexts[0], batch.KeyVersion, default));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            protector.ProtectAsync(new PiiBinding(Guid.Empty, RowId), [new PiiPlaintext("incidents.description", Address)], default));
    }

    [Fact]
    public async Task Each_value_gets_a_fresh_data_key_and_round_trips_under_the_server_chosen_version()
    {
        var vault = new FakePiiKeyVault();
        var protector = new PiiEnvelopeProtector(vault);

        var first = await protector.ProtectAsync(Row, [new PiiPlaintext("locations.address_text", Address)], default);
        var second = await protector.ProtectAsync(Row, [new PiiPlaintext("locations.address_text", Address)], default);

        Assert.Equal(vault.CurrentVersion, first.KeyVersion);
        Assert.NotEqual(first.Ciphertexts[0], second.Ciphertexts[0]);
        Assert.Equal(2, vault.WrappedKeys.Distinct(ByteArrayComparer.Instance).Count());
        Assert.Equal(Address, await protector.UnprotectAsync(Row, "locations.address_text", first.Ciphertexts[0], first.KeyVersion, default));
        AssertNoPlaintext(first.Ciphertexts[0], Address);
    }

    [Fact]
    public async Task Key_rotation_keeps_data_protected_under_the_previous_version_readable()
    {
        var vault = new FakePiiKeyVault();
        var protector = new PiiEnvelopeProtector(vault);
        var versionN = await protector.ProtectAsync(Row, [new PiiPlaintext("incidents.description", Address)], default);

        var rotatedTo = vault.Rotate();
        var versionNPlusOne = await protector.ProtectAsync(Row, [new PiiPlaintext("incidents.description", Phone)], default);

        Assert.NotEqual(versionN.KeyVersion, versionNPlusOne.KeyVersion);
        Assert.Equal(rotatedTo, versionNPlusOne.KeyVersion);
        Assert.Equal(Address, await protector.UnprotectAsync(Row, "incidents.description", versionN.Ciphertexts[0], versionN.KeyVersion, default));
        Assert.Equal(Phone, await protector.UnprotectAsync(Row, "incidents.description", versionNPlusOne.Ciphertexts[0], versionNPlusOne.KeyVersion, default));

        // A row cannot be relabelled with the newer version: the version is authenticated data.
        var relabelled = await Record.ExceptionAsync(() =>
            protector.UnprotectAsync(Row, "incidents.description", versionN.Ciphertexts[0], versionNPlusOne.KeyVersion, default));
        Assert.True(relabelled is PiiCiphertextRejectedException or PiiProtectionUnavailableException);
    }

    [Fact]
    public async Task Ciphertexts_are_bound_to_their_column_and_reject_tampering()
    {
        var vault = new FakePiiKeyVault();
        var protector = new PiiEnvelopeProtector(vault);
        var batch = await protector.ProtectAsync(Row, [new PiiPlaintext("locations.phone", Phone)], default);
        var ciphertext = batch.Ciphertexts[0];

        await Assert.ThrowsAsync<PiiCiphertextRejectedException>(() =>
            protector.UnprotectAsync(Row, "locations.contact_name", ciphertext, batch.KeyVersion, default));
        var tampered = (byte[])ciphertext.Clone();
        tampered[^1] ^= 0x01;
        await Assert.ThrowsAsync<PiiCiphertextRejectedException>(() =>
            protector.UnprotectAsync(Row, "locations.phone", tampered, batch.KeyVersion, default));
        await Assert.ThrowsAsync<PiiCiphertextRejectedException>(() =>
            protector.UnprotectAsync(Row, "locations.phone", [1, 2, 3], batch.KeyVersion, default));
    }

    [Theory]
    [InlineData(FakePiiKeyVault.Failure.CurrentVersion)]
    [InlineData(FakePiiKeyVault.Failure.Wrap)]
    public async Task An_unavailable_key_vault_fails_closed_without_leaking_the_value(FakePiiKeyVault.Failure failure)
    {
        var vault = new FakePiiKeyVault { FailOn = failure };
        var protector = new PiiEnvelopeProtector(vault);

        var exception = await Assert.ThrowsAsync<PiiProtectionUnavailableException>(() =>
            protector.ProtectAsync(Row, [new PiiPlaintext("locations.address_text", Address)], default));

        Assert.DoesNotContain(Address, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Location_protector_keeps_order_optional_values_and_one_version()
    {
        var vault = new FakePiiKeyVault();
        var envelope = new PiiEnvelopeProtector(vault);
        var protector = new AzureKeyVaultLocationPiiProtector(envelope);

        var all = await protector.ProtectAsync(new LocationPiiValues(Tenant, LocationId, Address, Contact, Phone), default);
        var addressOnly = await protector.ProtectAsync(new LocationPiiValues(Tenant, LocationId, Address, null, " "), default);
        var phoneOnly = await protector.ProtectAsync(new LocationPiiValues(Tenant, LocationId, Address, null, Phone), default);

        Assert.Equal(vault.CurrentVersion, all.KeyVersion);
        Assert.Equal(Address, await envelope.UnprotectAsync(LocationRow, AzureKeyVaultLocationPiiProtector.AddressTextPurpose, all.AddressTextCiphertext, all.KeyVersion, default));
        Assert.Equal(Contact, await envelope.UnprotectAsync(LocationRow, AzureKeyVaultLocationPiiProtector.ContactNamePurpose, all.ContactNameCiphertext!, all.KeyVersion, default));
        Assert.Equal(Phone, await envelope.UnprotectAsync(LocationRow, AzureKeyVaultLocationPiiProtector.PhonePurpose, all.PhoneCiphertext!, all.KeyVersion, default));
        Assert.Null(addressOnly.ContactNameCiphertext);
        Assert.Null(addressOnly.PhoneCiphertext);
        Assert.Null(phoneOnly.ContactNameCiphertext);
        Assert.Equal(Phone, await envelope.UnprotectAsync(LocationRow, AzureKeyVaultLocationPiiProtector.PhonePurpose, phoneOnly.PhoneCiphertext!, phoneOnly.KeyVersion, default));
    }

    [Fact]
    public async Task Module_protectors_translate_key_vault_failure_into_their_unavailable_exception()
    {
        var envelope = new PiiEnvelopeProtector(new FakePiiKeyVault { FailOn = FakePiiKeyVault.Failure.CurrentVersion });

        await Assert.ThrowsAsync<LocationPiiProtectionUnavailableException>(() =>
            new AzureKeyVaultLocationPiiProtector(envelope).ProtectAsync(new LocationPiiValues(Tenant, LocationId, Address, Contact, Phone), default));
        await Assert.ThrowsAsync<IncidentPiiProtectionUnavailableException>(() =>
            new AzureKeyVaultIncidentPiiProtector(envelope).ProtectAsync(new IncidentPiiBinding(Tenant, IncidentId), Address, default));
    }

    [Fact]
    public async Task Incident_protector_returns_the_key_vault_version_not_the_configured_label()
    {
        var vault = new FakePiiKeyVault();
        var envelope = new PiiEnvelopeProtector(vault);

        var result = await new AzureKeyVaultIncidentPiiProtector(envelope).ProtectAsync(new IncidentPiiBinding(Tenant, IncidentId), Address, default);

        Assert.Equal(vault.CurrentVersion, result.KeyVersion);
        Assert.NotEqual("inc001-v1", result.KeyVersion);
        Assert.Equal(Address, await envelope.UnprotectAsync(IncidentRow, AzureKeyVaultIncidentPiiProtector.DescriptionPurpose, result.Ciphertext, result.KeyVersion, default));
        AssertNoPlaintext(result.Ciphertext, Address);
    }

    [Fact]
    public async Task Health_check_fails_closed_when_the_key_cannot_wrap_and_is_neutral_when_not_selected()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new PiiProtectionOptions());
        var selected = new[] { new PiiKeyVaultSelection(() => true) };
        Assert.Equal(
            Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy,
            (await new PiiKeyWrapHealthCheck(Services(new FakePiiKeyVault()), selected, options, TimeProvider.System)
                .CheckHealthAsync(new(), default)).Status);
        Assert.Equal(
            Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy,
            (await new PiiKeyWrapHealthCheck(Services(new FakePiiKeyVault { FailOn = FakePiiKeyVault.Failure.Wrap }), selected, options, TimeProvider.System)
                .CheckHealthAsync(new(), default)).Status);

        // Not selected by any module: the key is never touched.
        var untouched = new FakePiiKeyVault { FailOn = FakePiiKeyVault.Failure.CurrentVersion };
        Assert.Equal(
            Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy,
            (await new PiiKeyWrapHealthCheck(Services(untouched), [new PiiKeyVaultSelection(() => false)], options, TimeProvider.System)
                .CheckHealthAsync(new(), default)).Status);
        Assert.Empty(untouched.WrappedKeys);
    }

    private static IServiceProvider Services(IPiiKeyWrapClient client) =>
        new Microsoft.Extensions.DependencyInjection.ServiceCollection()
            .AddSingleton(client)
            .BuildServiceProvider();

    [Theory]
    [InlineData("https://paquetenvia.vault.azure.net/keys/pii-envelope", true)]
    [InlineData("https://paquetenvia.vault.azure.net/keys/pii-envelope/", true)]
    [InlineData("https://paquetenvia.vault.azure.net/keys/pii-envelope/0123456789abcdef0123456789abcdef", false)]
    [InlineData("http://paquetenvia.vault.azure.net/keys/pii-envelope", false)]
    [InlineData("https://paquetenvia.vault.azure.net/secrets/pii-envelope", false)]
    [InlineData("https://paquetenvia.vault.azure.net/keys/pii-envelope?api-version=7.4", false)]
    [InlineData("https://paquetenvia.vault.azure.net:8443/keys/pii-envelope", false)]
    [InlineData("", false)]
    public void Only_a_versionless_https_key_uri_is_accepted(string keyId, bool valid)
    {
        Assert.Equal(valid, PiiProtectionOptionsValidator.IsValid(new PiiProtectionOptions
        {
            AzureKeyVault = new AzureKeyVaultPiiOptions { KeyId = keyId },
        }));
    }

    [Theory]
    [InlineData("akv:pii-envelope/0123456789abcdef0123456789abcdef", true)]
    [InlineData("akv:pii-envelope/0123456789ABCDEF0123456789ABCDEF", false)]
    [InlineData("akv:pii-envelope/latest", false)]
    [InlineData("geo001-mock-v1", false)]
    [InlineData("akv:/0123456789abcdef0123456789abcdef", false)]
    [InlineData("akv:other/key/0123456789abcdef0123456789abcdef", false)]
    public void Stored_key_versions_are_parsed_strictly(string value, bool valid)
    {
        Assert.Equal(valid, AzureKeyVaultPiiKeyWrapClient.TryParseKeyVersion(value, out _, out _));
    }

    private static void AssertNoPlaintext(byte[] ciphertext, string plaintext)
    {
        var needle = Encoding.UTF8.GetBytes(plaintext);
        Assert.True(ciphertext.AsSpan().IndexOf(needle) < 0, "The ciphertext contains the plaintext bytes.");
        Assert.DoesNotContain(plaintext, Encoding.UTF8.GetString(ciphertext), StringComparison.Ordinal);
        Assert.DoesNotContain(plaintext, Convert.ToBase64String(ciphertext), StringComparison.Ordinal);
    }

    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public static ByteArrayComparer Instance { get; } = new();

        public bool Equals(byte[]? x, byte[]? y) => x is not null && y is not null && x.AsSpan().SequenceEqual(y);

        public int GetHashCode(byte[] obj) => obj.Length == 0 ? 0 : obj[0] | (obj.Length << 8);
    }
}

/// <summary>An in-memory RSA key-encryption key with versions; never Azure.</summary>
public sealed class FakePiiKeyVault : IPiiKeyWrapClient
{
    private readonly Dictionary<string, RSA> _versions = new(StringComparer.Ordinal);

    public FakePiiKeyVault()
    {
        Rotate();
    }

    public enum Failure
    {
        None,
        CurrentVersion,
        Wrap,
        Unwrap,
    }

    public Failure FailOn { get; set; }

    public string CurrentVersion { get; private set; } = string.Empty;

    public List<byte[]> WrappedKeys { get; } = [];

    public string Rotate()
    {
        var version = $"akv:pii-envelope/{Guid.NewGuid():N}";
        _versions[version] = RSA.Create(2048);
        CurrentVersion = version;
        return version;
    }

    public Task<string> GetCurrentKeyVersionAsync(CancellationToken cancellationToken) =>
        FailOn == Failure.CurrentVersion
            ? Task.FromException<string>(new InvalidOperationException("Key Vault is unreachable."))
            : Task.FromResult(CurrentVersion);

    public Task<byte[]> WrapKeyAsync(string keyVersion, byte[] dataKey, CancellationToken cancellationToken)
    {
        if (FailOn == Failure.Wrap)
        {
            return Task.FromException<byte[]>(new InvalidOperationException("Key Vault is unreachable."));
        }

        var wrapped = _versions[keyVersion].Encrypt(dataKey, RSAEncryptionPadding.OaepSHA256);
        WrappedKeys.Add(wrapped);
        return Task.FromResult(wrapped);
    }

    public Task<byte[]> UnwrapKeyAsync(string keyVersion, byte[] wrappedKey, CancellationToken cancellationToken) =>
        FailOn == Failure.Unwrap
            ? Task.FromException<byte[]>(new InvalidOperationException("Key Vault is unreachable."))
            : Task.FromResult(_versions[keyVersion].Decrypt(wrappedKey, RSAEncryptionPadding.OaepSHA256));
}
