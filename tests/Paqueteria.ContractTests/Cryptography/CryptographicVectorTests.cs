using System.Security.Cryptography;
using System.Text;
using System.Globalization;

namespace Paqueteria.ContractTests.Cryptography;

public sealed class CryptographicVectorTests
{
    private static readonly OrderAcceptanceEvidence AcceptanceVector = new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Guid.Parse("33333333-3333-3333-3333-333333333333"),
        Guid.Parse("44444444-4444-4444-4444-444444444444"),
        "terms-2026-07",
        "privacy-2026-07",
        DateTimeOffset.Parse("2026-07-20T12:34:56.1234560Z", CultureInfo.InvariantCulture),
        "pwa");

    [Fact]
    public void Tracking_token_matches_the_permanent_base64url_and_sha256_vector()
    {
        var raw = Convert.FromHexString("0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20");
        var token = TrackingTokenReference.Encode(raw);
        Assert.Equal("AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA", token);
        Assert.DoesNotContain('=', token);
        Assert.Equal("eb9f16800c9029ffca85695763d23c3ace71011cf40e9354acd810205e250f87", Convert.ToHexString(TrackingTokenReference.Hash(token)).ToLowerInvariant());

        var mutated = raw.ToArray();
        mutated[0] ^= 0x01;
        Assert.NotEqual(TrackingTokenReference.Hash(token), TrackingTokenReference.Hash(TrackingTokenReference.Encode(mutated)));
        Assert.NotEqual(TrackingTokenReference.Hash(token), TrackingTokenReference.Hash(token + "="));
        Assert.NotEqual(TrackingTokenReference.Hash(token), TrackingTokenReference.Hash(token.ToLowerInvariant()));
        Assert.Throws<ArgumentException>(() => TrackingTokenReference.Encode(raw[..^1]));
    }

    [Fact]
    public void Acceptance_canonicalizer_matches_the_permanent_utf8_and_hash_vectors()
    {
        const string expected = "{\"schema_version\":\"order-acceptance-v1\",\"order_id\":\"11111111-1111-1111-1111-111111111111\",\"quote_id\":\"22222222-2222-2222-2222-222222222222\",\"owner_org_id\":\"33333333-3333-3333-3333-333333333333\",\"actor_id\":\"44444444-4444-4444-4444-444444444444\",\"terms_version\":\"terms-2026-07\",\"privacy_version\":\"privacy-2026-07\",\"accepted_at_client\":\"2026-07-20T12:34:56.1234560Z\",\"acceptance_channel\":\"PWA\"}";
        var canonical = OrderAcceptanceCanonicalizer.Canonicalize(AcceptanceVector);
        Assert.Equal(Encoding.UTF8.GetBytes(expected), canonical);
        Assert.False(canonical.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        var hash = OrderAcceptanceCanonicalizer.Hash(AcceptanceVector);
        Assert.Equal("2a09176e270ddcc52e0fee157f3d5bd869f36047f7f946daa7caed4816ae0b37", Convert.ToHexString(hash).ToLowerInvariant());
        Assert.Equal("KgkXbicN3MUuD+4Vfz1b2GnzYEf3+Ubap8rtSBauCzc=", Convert.ToBase64String(hash));
    }

    [Fact]
    public void Acceptance_hash_changes_for_noncanonical_variants_and_unicode_is_deterministic()
    {
        var canonical = OrderAcceptanceCanonicalizer.Canonicalize(AcceptanceVector);
        var reordered = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(canonical).Replace(
            "{\"schema_version\":\"order-acceptance-v1\",\"order_id\"",
            "{\"order_id\""));
        Assert.NotEqual(SHA256.HashData(canonical), SHA256.HashData(reordered));

        var changedTimestamp = AcceptanceVector with { AcceptedAtClient = AcceptanceVector.AcceptedAtClient.AddTicks(1) };
        var changedChannel = AcceptanceVector with { AcceptanceChannel = "api" };
        Assert.NotEqual(OrderAcceptanceCanonicalizer.Hash(AcceptanceVector), OrderAcceptanceCanonicalizer.Hash(changedTimestamp));
        Assert.NotEqual(OrderAcceptanceCanonicalizer.Hash(AcceptanceVector), OrderAcceptanceCanonicalizer.Hash(changedChannel));
        Assert.NotEqual(SHA256.HashData(canonical), SHA256.HashData([.. Encoding.UTF8.Preamble, .. canonical]));

        var unicode = AcceptanceVector with { TermsVersion = "términos-ñ" };
        Assert.Equal(OrderAcceptanceCanonicalizer.Canonicalize(unicode), OrderAcceptanceCanonicalizer.Canonicalize(unicode));
    }
}
