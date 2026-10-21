using System.Security.Cryptography;
using System.Text;
using Locations.Application.Geocoding;
using Locations.Application.Locations;
using Locations.Infrastructure.Geocoding;
using Locations.Infrastructure.Locations;
using NetTopologySuite.Geometries;
using Paqueteria.Infrastructure.Security.Pii;
using Paqueteria.UnitTests.Security;
using DomainLocation = Locations.Domain.Location;

namespace Paqueteria.UnitTests.Locations;

/// <summary>
/// NTF-RECIPIENT-NOTICES-2026-10-20 (E4-P2 "Llave propia para avisos", E4-P17 "Dos copias, como el teléfono"; AI-08
/// NTF-002): the destination e-mail under the PII key, the notice copies under the recipient-notice key (only on a
/// destination), the sealers the API can only wrap with, the domain checks that mirror AI-06 and the redacted
/// <c>ToString</c> of every contact type.
/// </summary>
public sealed class Ntf002RecipientNoticeContactTests
{
    private const string Email = "destinatario@example.invalid";
    private const string Phone = "6672222222";
    private static readonly Guid OrganizationId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid LocationId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    [Fact]
    public async Task The_key_vault_pii_protector_seals_the_email_under_its_own_purpose_and_version()
    {
        var vault = new FakePiiKeyVault();
        var envelope = new PiiEnvelopeProtector(vault);
        var protector = new AzureKeyVaultLocationPiiProtector(envelope);

        var sealedPii = await protector.ProtectAsync(
            new LocationPiiValues(OrganizationId, LocationId, "Calle Sintetica 200", "Destino Sintetico", Phone, Email),
            default);

        Assert.Equal("locations.email", AzureKeyVaultLocationPiiProtector.EmailPurpose);
        Assert.NotNull(sealedPii.EmailCiphertext);
        var row = new PiiBinding(OrganizationId, LocationId);
        Assert.Equal(Email, await envelope.UnprotectAsync(row, "locations.email", sealedPii.EmailCiphertext, sealedPii.KeyVersion, default));
        Assert.Equal(Phone, await envelope.UnprotectAsync(row, "locations.phone", sealedPii.PhoneCiphertext!, sealedPii.KeyVersion, default));
        await Assert.ThrowsAsync<PiiCiphertextRejectedException>(() =>
            envelope.UnprotectAsync(row, "locations.phone", sealedPii.EmailCiphertext, sealedPii.KeyVersion, default));
        Assert.True(sealedPii.EmailCiphertext.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Email)) < 0);

        var withoutEmail = await protector.ProtectAsync(
            new LocationPiiValues(OrganizationId, LocationId, "Calle Sintetica 200", "Destino Sintetico", Phone),
            default);
        Assert.Null(withoutEmail.EmailCiphertext);
        Assert.NotNull(withoutEmail.PhoneCiphertext);
    }

    [Fact]
    public async Task The_mock_pii_protector_keeps_its_bytes_and_digests_the_email_too()
    {
        var protector = new DeterministicMockLocationPiiProtector();
        var withEmail = await protector.ProtectAsync(
            new LocationPiiValues(OrganizationId, LocationId, "Calle Sintetica 200", "Destino Sintetico", Phone, Email), default);
        var withoutEmail = await protector.ProtectAsync(
            new LocationPiiValues(OrganizationId, LocationId, "Calle Sintetica 200", "Destino Sintetico", Phone), default);

        Assert.Equal(protector.Protect(Email, DeterministicMockLocationPiiProtector.KeyVersion), withEmail.EmailCiphertext);
        Assert.Null(withoutEmail.EmailCiphertext);
        Assert.Equal(withoutEmail.PhoneCiphertext, withEmail.PhoneCiphertext);
        Assert.Equal(withoutEmail.AddressTextCiphertext, withEmail.AddressTextCiphertext);
    }

    [Fact]
    public async Task Disabled_writes_no_notice_copy()
    {
        Assert.Null(await new DisabledLocationNoticeContactSealer().SealAsync(Values(Email), default));
    }

    [Fact]
    public async Task The_mock_notice_sealer_is_synthetic_one_way_and_distinct_from_the_pii_mock()
    {
        var sealer = new DeterministicMockLocationNoticeContactSealer();
        var first = await sealer.SealAsync(Values(Email), default);
        var second = await sealer.SealAsync(Values(Email), default);
        var phoneOnly = await sealer.SealAsync(Values(null), default);

        Assert.NotNull(first);
        Assert.Equal("ntf002-notice-mock-v1", first.KeyVersion);
        Assert.Equal(first.PhoneCiphertext, second!.PhoneCiphertext);
        Assert.Equal(32, first.PhoneCiphertext.Length);
        Assert.NotNull(first.EmailCiphertext);
        Assert.Null(phoneOnly!.EmailCiphertext);
        Assert.NotEqual(new DeterministicMockLocationPiiProtector().Protect(Phone, DeterministicMockLocationPiiProtector.KeyVersion), first.PhoneCiphertext);
        Assert.DoesNotContain(Phone, Convert.ToHexString(first.PhoneCiphertext), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_key_vault_notice_sealer_binds_each_copy_to_the_row_and_purpose_and_only_the_private_key_opens_it()
    {
        using var rsa = RSA.Create(3072);
        var version = $"akv:recipient-notice-kek/{Guid.NewGuid():N}";
        var publicOnly = new PublicKeyWrapClient(rsa.ExportParameters(includePrivateParameters: false), version);
        var sealer = new AzureKeyVaultLocationNoticeContactSealer(new RecipientNoticeEnvelopeSealer(publicOnly));

        var copies = await sealer.SealAsync(Values(Email), default);

        Assert.NotNull(copies);
        Assert.Equal(version, copies.KeyVersion);
        Assert.Equal("locations.notice_phone", AzureKeyVaultLocationNoticeContactSealer.NoticePhonePurpose);
        Assert.Equal("locations.notice_email", AzureKeyVaultLocationNoticeContactSealer.NoticeEmailPurpose);
        Assert.Equal(2, publicOnly.Wraps);

        // What the Worker will do with Key Vault unwrapKey (AI-08 NTF-005), here with the private key of the test.
        var worker = new PiiEnvelopeProtector(new PrivateKeyClient(rsa, version));
        var row = new PiiBinding(OrganizationId, LocationId);
        Assert.Equal(Phone, await worker.UnprotectAsync(row, "locations.notice_phone", copies.PhoneCiphertext, version, default));
        Assert.Equal(Email, await worker.UnprotectAsync(row, "locations.notice_email", copies.EmailCiphertext!, version, default));
        await Assert.ThrowsAsync<PiiCiphertextRejectedException>(() =>
            worker.UnprotectAsync(row, "locations.phone", copies.PhoneCiphertext, version, default));
        await Assert.ThrowsAsync<PiiCiphertextRejectedException>(() =>
            worker.UnprotectAsync(new PiiBinding(OrganizationId, Guid.NewGuid()), "locations.notice_phone", copies.PhoneCiphertext, version, default));
        await Assert.ThrowsAsync<PiiCiphertextRejectedException>(() =>
            worker.UnprotectAsync(new PiiBinding(Guid.NewGuid(), LocationId), "locations.notice_email", copies.EmailCiphertext!, version, default));

        // A different key (the PII key, say) cannot open a notice copy.
        using var otherKey = RSA.Create(3072);
        var other = new PiiEnvelopeProtector(new PrivateKeyClient(otherKey, version));
        await Assert.ThrowsAnyAsync<Exception>(() =>
            other.UnprotectAsync(row, "locations.notice_phone", copies.PhoneCiphertext, version, default));

        var phoneOnly = await sealer.SealAsync(Values(null), default);
        Assert.Null(phoneOnly!.EmailCiphertext);
    }

    [Fact]
    public async Task A_failing_notice_key_fails_closed_without_the_values()
    {
        var sealer = new AzureKeyVaultLocationNoticeContactSealer(new RecipientNoticeEnvelopeSealer(new FailingWrapClient()));

        var failure = await Assert.ThrowsAsync<LocationPiiProtectionUnavailableException>(() => sealer.SealAsync(Values(Email), default));

        Assert.DoesNotContain(Email, failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Phone, failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_a_destination_gets_the_email_and_the_notice_copies_and_the_phone_copy_is_normalized()
    {
        var pii = new CapturingPiiProtector();
        var sealer = new CapturingNoticeSealer();
        var service = new PostgreSqlLocationService(null!, new ManualGeocodingProvider(), pii, sealer, null!, new FixedClock());

        // The transaction context is absent, so the call stops right after protection: what reached the protector
        // and the sealer is all that matters here.
        await Assert.ThrowsAsync<LocationServiceUnavailableException>(() =>
            service.ResolveAsync(Command(QuoteLocationRole.Destination, Email), default));
        Assert.Equal(Email, pii.Captured!.Email);
        Assert.Equal(Phone, sealer.Captured!.Phone);
        Assert.Equal(Email, sealer.Captured.Email);
        Assert.Equal(pii.Captured.LocationId, sealer.Captured.LocationId);

        pii.Captured = null;
        sealer.Captured = null;
        await Assert.ThrowsAsync<LocationServiceUnavailableException>(() =>
            service.ResolveAsync(Command(QuoteLocationRole.Destination, null), default));
        Assert.Null(pii.Captured!.Email);
        Assert.Null(sealer.Captured!.Email);

        pii.Captured = null;
        sealer.Captured = null;
        await Assert.ThrowsAsync<LocationServiceUnavailableException>(() =>
            service.ResolveAsync(Command(QuoteLocationRole.Origin, null), default));
        Assert.NotNull(pii.Captured);
        Assert.Null(sealer.Captured);
    }

    [Theory]
    [InlineData(QuoteLocationRole.Origin, Email)]
    [InlineData(QuoteLocationRole.Destination, " destinatario@example.invalid")]
    [InlineData(QuoteLocationRole.Destination, "destinatario@EXAMPLE.invalid")]
    [InlineData(QuoteLocationRole.Destination, "destinatario")]
    public async Task An_origin_email_or_a_non_normalized_email_is_refused_before_protection(QuoteLocationRole role, string email)
    {
        var pii = new CapturingPiiProtector();
        var sealer = new CapturingNoticeSealer();
        var service = new PostgreSqlLocationService(null!, new ManualGeocodingProvider(), pii, sealer, null!, new FixedClock());

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => service.ResolveAsync(Command(role, email), default));

        Assert.Null(pii.Captured);
        Assert.Null(sealer.Captured);
        Assert.DoesNotContain(email.Trim(), refused.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_partial_notice_copy_fails_closed()
    {
        var pii = new CapturingPiiProtector();
        var sealer = new CapturingNoticeSealer { DropEmailCopy = true };
        var service = new PostgreSqlLocationService(null!, new ManualGeocodingProvider(), pii, sealer, null!, new FixedClock());

        await Assert.ThrowsAsync<LocationPiiProtectionUnavailableException>(() =>
            service.ResolveAsync(Command(QuoteLocationRole.Destination, Email), default));
    }

    [Fact]
    public void The_domain_mirrors_the_AI06_checks()
    {
        DomainLocation Create(byte[]? email, byte[]? noticePhone, byte[]? noticeEmail, string? noticeVersion) => new(
            LocationId, OrganizationId, Guid.NewGuid(), null, null, new Point(-107.4, 24.8) { SRID = 4326 },
            [1], "Synthetic quote destination", [2], [3], "akv:pii-envelope/v1", DateTimeOffset.UtcNow,
            email, noticePhone, noticeEmail, noticeVersion);

        var full = Create([4], [5], [6], "akv:recipient-notice-kek/v1");
        Assert.Equal([5], full.NoticePhoneCiphertext);
        Assert.Equal("akv:recipient-notice-kek/v1", full.NoticeKeyVersion);
        Assert.Null(Create(null, null, null, null).NoticeKeyVersion);
        Assert.NotNull(Create([4], null, null, null).EmailCiphertext);
        Assert.NotNull(Create(null, [5], null, "akv:recipient-notice-kek/v1").NoticePhoneCiphertext);

        Assert.Throws<ArgumentException>(() => Create(null, [5], null, null));
        Assert.Throws<ArgumentException>(() => Create(null, null, null, "akv:recipient-notice-kek/v1"));
        Assert.Throws<ArgumentException>(() => Create(null, [5], [6], "akv:recipient-notice-kek/v1"));
        Assert.Throws<ArgumentException>(() => Create([4], [5], [6], " "));
        Assert.Throws<ArgumentException>(() => Create([], null, null, null));
        Assert.Throws<ArgumentException>(() => Create(null, [], null, "akv:recipient-notice-kek/v1"));
    }

    [Fact]
    public void Contact_types_never_print_a_value_or_a_ciphertext()
    {
        var ciphertext = new byte[] { 0xCA, 0xFE, 0xBA, 0xBE };
        foreach (var printed in new[]
                 {
                     new LocationPiiValues(OrganizationId, LocationId, "Calle Sintetica 200", "Destino Sintetico", Phone, Email).ToString(),
                     new ProtectedLocationPii("v1", ciphertext, ciphertext, ciphertext, ciphertext).ToString(),
                     Values(Email).ToString(),
                     new SealedLocationNoticeContact("v1", ciphertext, ciphertext).ToString(),
                     Command(QuoteLocationRole.Destination, Email).ToString(),
                 })
        {
            Assert.DoesNotContain(Email, printed, StringComparison.Ordinal);
            Assert.DoesNotContain(Phone, printed, StringComparison.Ordinal);
            Assert.DoesNotContain("Calle Sintetica", printed, StringComparison.Ordinal);
            Assert.DoesNotContain("Destino Sintetico", printed, StringComparison.Ordinal);
            Assert.DoesNotContain("Byte", printed, StringComparison.Ordinal);
            Assert.DoesNotContain("CAFE", printed, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static LocationNoticeContactValues Values(string? email) => new(OrganizationId, LocationId, Phone, email);

    private static ResolveQuoteLocationCommand Command(QuoteLocationRole role, string? email) => new(
        Guid.Parse("55555555-5555-5555-5555-555555555555"),
        OrganizationId,
        "prcloc-0123456789abcdef0123456789abcdef",
        role,
        "Calle Sintetica 200",
        "Destino Sintetico",
        Phone,
        null,
        24.81,
        -107.41,
        "request-1",
        email);

    private sealed class FixedClock : Paqueteria.Application.IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class CapturingPiiProtector : ILocationPiiProtector
    {
        public LocationPiiValues? Captured { get; set; }

        public Task<ProtectedLocationPii> ProtectAsync(LocationPiiValues values, CancellationToken cancellationToken)
        {
            Captured = values;
            return Task.FromResult(new ProtectedLocationPii(
                "akv:pii-envelope/v1",
                [1],
                values.ContactName is null ? null : [2],
                values.Phone is null ? null : [3],
                values.Email is null ? null : [4]));
        }
    }

    private sealed class CapturingNoticeSealer : ILocationNoticeContactSealer
    {
        public LocationNoticeContactValues? Captured { get; set; }

        public bool DropEmailCopy { get; init; }

        public Task<SealedLocationNoticeContact?> SealAsync(LocationNoticeContactValues values, CancellationToken cancellationToken)
        {
            Captured = values;
            return Task.FromResult<SealedLocationNoticeContact?>(new SealedLocationNoticeContact(
                "akv:recipient-notice-kek/v1",
                [5],
                values.Email is null || DropEmailCopy ? null : [6]));
        }
    }

    /// <summary>Wraps with the public part only, as the API does; it has no way to unwrap.</summary>
    private sealed class PublicKeyWrapClient(RSAParameters publicKey, string version) : IRecipientNoticeKeyWrapClient
    {
        public int Wraps { get; private set; }

        public Task<string> GetCurrentKeyVersionAsync(CancellationToken cancellationToken) => Task.FromResult(version);

        public Task<byte[]> WrapKeyAsync(string keyVersion, byte[] dataKey, CancellationToken cancellationToken)
        {
            Assert.Equal(version, keyVersion);
            Wraps++;
            using var rsa = RSA.Create(publicKey);
            return Task.FromResult(rsa.Encrypt(dataKey, RSAEncryptionPadding.OaepSHA256));
        }
    }

    /// <summary>The Worker side, standing in for Key Vault unwrapKey with RSA-OAEP-256.</summary>
    private sealed class PrivateKeyClient(RSA rsa, string version) : IPiiKeyWrapClient
    {
        public Task<string> GetCurrentKeyVersionAsync(CancellationToken cancellationToken) => Task.FromResult(version);

        public Task<byte[]> WrapKeyAsync(string keyVersion, byte[] dataKey, CancellationToken cancellationToken) =>
            Task.FromResult(rsa.Encrypt(dataKey, RSAEncryptionPadding.OaepSHA256));

        public Task<byte[]> UnwrapKeyAsync(string keyVersion, byte[] wrappedKey, CancellationToken cancellationToken) =>
            Task.FromResult(rsa.Decrypt(wrappedKey, RSAEncryptionPadding.OaepSHA256));
    }

    private sealed class FailingWrapClient : IRecipientNoticeKeyWrapClient
    {
        public Task<string> GetCurrentKeyVersionAsync(CancellationToken cancellationToken) =>
            Task.FromException<string>(new InvalidOperationException("Synthetic Key Vault outage."));

        public Task<byte[]> WrapKeyAsync(string keyVersion, byte[] dataKey, CancellationToken cancellationToken) =>
            Task.FromException<byte[]>(new InvalidOperationException("Synthetic Key Vault outage."));
    }
}
