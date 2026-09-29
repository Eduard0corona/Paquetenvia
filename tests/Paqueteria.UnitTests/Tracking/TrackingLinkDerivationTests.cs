using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Options;
using Orders.Application.Tracking;
using Orders.Infrastructure;
using Orders.Infrastructure.Tracking;
using Paqueteria.Contracts.Tracking;

namespace Paqueteria.UnitTests.Tracking;

/// <summary>
/// TRK-002-AUTO-LINK: the link token is Base64URL (no padding) of HMAC-SHA256 over the exact versioned canonical
/// input, deterministic for an order generation and different for any other generation, order or key; the key ring
/// accepts only valid keys and fails closed at start when a key is required; the lifetime policy mirrors the SQL.
/// </summary>
public sealed class TrackingLinkDerivationTests
{
    /// <summary>A synthetic key derived at runtime from a public label; no key-like literal is committed.</summary>
    private static readonly byte[] Key = SHA256.HashData(Encoding.UTF8.GetBytes("trk-002 auto-link unit vector key"));

    private static readonly Guid OrderId = Guid.Parse("0f1e2d3c-4b5a-4968-8776-a5b4c3d2e1f0");

    [Fact]
    public void Canonical_input_is_exact_versioned_and_lowercase()
    {
        Assert.Equal(
            "paquetenvia-trk-v1|1|0f1e2d3c-4b5a-4968-8776-a5b4c3d2e1f0|1",
            TrackingLinkTokenDerivation.CanonicalInput(1, OrderId, 1));
        Assert.Equal(
            "paquetenvia-trk-v1|32767|0f1e2d3c-4b5a-4968-8776-a5b4c3d2e1f0|2147483647",
            TrackingLinkTokenDerivation.CanonicalInput(32767, Guid.Parse("0F1E2D3C-4B5A-4968-8776-A5B4C3D2E1F0"), int.MaxValue));
    }

    [Fact]
    public void Fixed_synthetic_key_vector_matches_an_independent_HMAC_computed_at_test_time()
    {
        // Independent computation: HMAC-SHA256 through the HMAC class over the literal canonical bytes, then
        // RFC 4648 section 5 by hand. The derivation must agree byte for byte.
        using var hmac = new HMACSHA256(Key);
        var digest = hmac.ComputeHash(Encoding.UTF8.GetBytes("paquetenvia-trk-v1|1|0f1e2d3c-4b5a-4968-8776-a5b4c3d2e1f0|1"));
        Assert.Equal(32, digest.Length);
        var expected = new StringBuilder(Convert.ToBase64String(digest));
        expected.Replace('+', '-').Replace('/', '_');
        var vector = expected.ToString().TrimEnd('=');

        var token = TrackingLinkTokenDerivation.DeriveToken(Key, 1, OrderId, 1);
        Assert.Equal(vector, token);
        Assert.Equal(TrackingLinkTokenDerivation.TokenLength, token.Length);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", token);

        // The stored value is unchanged by derivation: SHA-256 over the exact UTF-8 bytes of the token.
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(vector)), new TrackingTokenHasher().HashToken(token));
    }

    [Fact]
    public void Derivation_is_deterministic_per_generation_and_distinct_otherwise()
    {
        var first = TrackingLinkTokenDerivation.DeriveToken(Key, 1, OrderId, 1);
        Assert.Equal(first, TrackingLinkTokenDerivation.DeriveToken(Key, 1, OrderId, 1));

        var others = new[]
        {
            TrackingLinkTokenDerivation.DeriveToken(Key, 1, OrderId, 2),
            TrackingLinkTokenDerivation.DeriveToken(Key, 2, OrderId, 1),
            TrackingLinkTokenDerivation.DeriveToken(Key, 1, Guid.Parse("0f1e2d3c-4b5a-4968-8776-a5b4c3d2e1f1"), 1),
            TrackingLinkTokenDerivation.DeriveToken(
                SHA256.HashData(Encoding.UTF8.GetBytes("trk-002 auto-link unit other key")), 1, OrderId, 1),
        };
        Assert.All(others, other => Assert.NotEqual(first, other));
        Assert.Equal(others.Length, others.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(32768, 1)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    public void Out_of_range_versions_and_generations_are_refused(int keyVersion, int generation) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TrackingLinkTokenDerivation.DeriveToken(Key, keyVersion, OrderId, generation));

    [Fact]
    public void Empty_orders_and_short_or_long_keys_are_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TrackingLinkTokenDerivation.DeriveToken(Key, 1, Guid.Empty, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TrackingLinkTokenDerivation.DeriveToken(new byte[31], 1, OrderId, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TrackingLinkTokenDerivation.DeriveToken(new byte[129], 1, OrderId, 1));
    }

    [Fact]
    public void Key_ring_re_derives_configured_versions_and_the_synthetic_key_exists_only_when_allowed()
    {
        var other = SHA256.HashData(Encoding.UTF8.GetBytes("trk-002 auto-link unit second key"));
        var ring = new PublicTrackingLinkKeyRing(
            new PublicTrackingOptions
            {
                CurrentLinkKeyVersion = 2,
                LinkKeys = { [1] = Convert.ToBase64String(Key), [2] = Convert.ToBase64String(other) },
            },
            allowSyntheticKey: true);
        Assert.True(ring.IsAvailable);
        Assert.False(ring.IsSynthetic);
        Assert.Equal(2, ring.CurrentKeyVersion);
        Assert.Equal(TrackingLinkTokenDerivation.DeriveToken(other, 2, OrderId, 3), ring.DeriveCurrent(OrderId, 3));
        Assert.True(ring.TryDerive(1, OrderId, 1, out var old));
        Assert.Equal(TrackingLinkTokenDerivation.DeriveToken(Key, 1, OrderId, 1), old);
        Assert.False(ring.TryDerive(3, OrderId, 1, out _));

        var synthetic = new PublicTrackingLinkKeyRing(new PublicTrackingOptions(), allowSyntheticKey: true);
        Assert.True(synthetic.IsAvailable);
        Assert.True(synthetic.IsSynthetic);
        Assert.Equal(1, synthetic.CurrentKeyVersion);

        var none = new PublicTrackingLinkKeyRing(new PublicTrackingOptions(), allowSyntheticKey: false);
        Assert.False(none.IsAvailable);
        Assert.Throws<PublicTrackingTokenInfrastructureException>(() => none.DeriveCurrent(OrderId, 1));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("DevSynthetic")]
    public void Start_fails_closed_without_a_link_key_outside_development_and_testing(string environment)
    {
        var failure = Assert.Throws<OptionsValidationException>(() =>
            Resolve(environment, new Dictionary<string, string?>
            {
                ["PublicTracking:Provider"] = "PostgreSql",
                ["PublicTracking:PublicBaseUrl"] = "https://paquetenvia.com",
            }));
        Assert.Contains(
            "PublicTracking:LinkKeys is required",
            string.Join(' ', failure.Failures),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("c2hvcnQ=")]
    [InlineData("not base64 at all")]
    public void Start_fails_closed_on_a_short_or_malformed_key_in_every_environment(string key)
    {
        foreach (var environment in new[] { "Production", "Development", "Testing" })
        {
            var failure = Assert.Throws<OptionsValidationException>(() =>
                Resolve(environment, new Dictionary<string, string?>
                {
                    ["PublicTracking:Provider"] = "PostgreSql",
                    ["PublicTracking:PublicBaseUrl"] = "https://paquetenvia.com",
                    ["PublicTracking:LinkKeys:1"] = key,
                }));
            Assert.Contains("LinkKeys with versions", string.Join(' ', failure.Failures), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Start_fails_closed_when_the_current_version_has_no_key()
    {
        var failure = Assert.Throws<OptionsValidationException>(() =>
            Resolve("Production", new Dictionary<string, string?>
            {
                ["PublicTracking:Provider"] = "PostgreSql",
                ["PublicTracking:PublicBaseUrl"] = "https://paquetenvia.com",
                ["PublicTracking:CurrentLinkKeyVersion"] = "2",
                ["PublicTracking:LinkKeys:1"] = Convert.ToBase64String(Key),
            }));
        Assert.Contains("CurrentLinkKeyVersion", string.Join(' ', failure.Failures), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("http://paquetenvia.com")]
    [InlineData("https://paquetenvia.com/track")]
    [InlineData("https://paquetenvia.com/?x=1")]
    [InlineData("https://user@paquetenvia.com")]
    [InlineData("https://*.paquetenvia.com")]
    [InlineData("paquetenvia.com")]
    public void Production_requires_an_https_origin_as_public_base_url(string? baseUrl)
    {
        var failure = Assert.Throws<OptionsValidationException>(() =>
            Resolve("Production", new Dictionary<string, string?>
            {
                ["PublicTracking:Provider"] = "PostgreSql",
                ["PublicTracking:PublicBaseUrl"] = baseUrl,
                ["PublicTracking:LinkKeys:1"] = Convert.ToBase64String(Key),
            }));
        Assert.Contains("PublicBaseUrl", string.Join(' ', failure.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public void Valid_production_configuration_starts_and_development_may_use_the_synthetic_key_and_loopback()
    {
        var production = Resolve("Production", new Dictionary<string, string?>
        {
            ["PublicTracking:Provider"] = "PostgreSql",
            ["PublicTracking:PublicBaseUrl"] = "https://paquetenvia.com",
            ["PublicTracking:LinkKeys:1"] = Convert.ToBase64String(Key),
        });
        Assert.False(production.IsSynthetic);

        var development = Resolve("Development", new Dictionary<string, string?>
        {
            ["PublicTracking:Provider"] = "PostgreSql",
            ["PublicTracking:PublicBaseUrl"] = "http://127.0.0.1:3000",
        });
        Assert.True(development.IsSynthetic);

        // Tracking disabled: nothing is required, nothing is derived.
        var disabled = Resolve("Production", new Dictionary<string, string?>());
        Assert.False(disabled.IsAvailable);
    }

    [Fact]
    public void Lifetime_policy_is_the_order_lifecycle_plus_24_hours_after_the_first_final_event()
    {
        var now = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
        var finishedAt = now.AddHours(-3);
        foreach (var status in Enum.GetValues<PublicOrderStatus>())
        {
            var final = status is PublicOrderStatus.Delivered or PublicOrderStatus.Returned or PublicOrderStatus.Cancelled;
            Assert.Equal(final, PublicTrackingLinkPolicy.IsFinal(status));
            Assert.Equal(
                final ? finishedAt.AddHours(24) : null,
                PublicTrackingLinkPolicy.ValidUntil(status, finishedAt, now));
        }

        // RESCHEDULED is SCHEDULED for the public: in progress.
        Assert.False(PublicTrackingLinkPolicy.IsFinal(new PublicOrderStatusPolicy().Map("RESCHEDULED")));
        Assert.True(PublicTrackingLinkPolicy.IsFinal(new PublicOrderStatusPolicy().Map("CLAIM_RESOLVED")));

        // A finished order without its final event is already expired, as in SQL.
        Assert.Equal(now, PublicTrackingLinkPolicy.ValidUntil(PublicOrderStatus.Delivered, null, now));
        Assert.Equal(["DELIVERED", "RETURNED", "CANCELLED"], PublicTrackingLinkPolicy.FinalPublicEventCodes);
    }

    [Fact]
    public void Public_url_is_the_base_origin_track_and_the_token()
    {
        var token = TrackingLinkTokenDerivation.DeriveToken(Key, 1, OrderId, 1);
        foreach (var allowLoopbackHttp in new[] { false, true })
        {
            Assert.Equal(
                $"https://paquetenvia.com/track/{token}",
                PublicTrackingLinkPolicy.BuildUrl("https://paquetenvia.com", token, allowLoopbackHttp));
            Assert.Equal(
                $"https://paquetenvia.com/track/{token}",
                PublicTrackingLinkPolicy.BuildUrl("https://paquetenvia.com/", token, allowLoopbackHttp));
            Assert.Throws<ArgumentException>(() =>
                PublicTrackingLinkPolicy.BuildUrl("https://paquetenvia.com", "short", allowLoopbackHttp));
            Assert.Throws<ArgumentException>(() =>
                PublicTrackingLinkPolicy.BuildUrl("ftp://paquetenvia.com", token, allowLoopbackHttp));
            Assert.Throws<ArgumentException>(() =>
                PublicTrackingLinkPolicy.BuildUrl("http://paquetenvia.com", token, allowLoopbackHttp));
        }

        Assert.False(PublicTrackingLinkPolicy.IsValidPublicBaseUrl("http://127.0.0.1:3000"));
        Assert.True(PublicTrackingLinkPolicy.IsValidPublicBaseUrl("http://127.0.0.1:3000", allowLoopbackHttp: true));
        Assert.False(PublicTrackingLinkPolicy.IsValidPublicBaseUrl("http://paquetenvia.com", allowLoopbackHttp: true));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("DevSynthetic")]
    public void Outside_development_and_testing_an_http_loopback_base_never_builds_a_link(string environment)
    {
        var token = TrackingLinkTokenDerivation.DeriveToken(Key, 1, OrderId, 1);
        var policy = ResolveBaseUrlPolicy(environment);
        Assert.False(policy.AllowLoopbackHttp);
        foreach (var baseUrl in new[] { "http://127.0.0.1:3000", "http://localhost:3000", "http://[::1]:3000" })
        {
            Assert.False(policy.IsValid(baseUrl));
            Assert.Throws<ArgumentException>(() => policy.BuildUrl(baseUrl, token));
            Assert.Throws<ArgumentException>(() =>
                PublicTrackingLinkPolicy.BuildUrl(baseUrl, token, allowLoopbackHttp: false));
        }

        Assert.Equal($"https://paquetenvia.com/track/{token}", policy.BuildUrl("https://paquetenvia.com", token));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void Development_and_testing_build_links_on_an_http_loopback_base(string environment)
    {
        var token = TrackingLinkTokenDerivation.DeriveToken(Key, 1, OrderId, 1);
        var policy = ResolveBaseUrlPolicy(environment);
        Assert.True(policy.AllowLoopbackHttp);
        Assert.Equal($"http://127.0.0.1:3000/track/{token}", policy.BuildUrl("http://127.0.0.1:3000", token));
        Assert.Equal($"http://127.0.0.1:3000/track/{token}", policy.BuildUrl("http://127.0.0.1:3000/", token));
        Assert.Equal($"https://paquetenvia.com/track/{token}", policy.BuildUrl("https://paquetenvia.com", token));

        // Only loopback: a public http host is refused even here.
        Assert.Throws<ArgumentException>(() => policy.BuildUrl("http://paquetenvia.com", token));
    }

    private static PublicTrackingBaseUrlPolicy ResolveBaseUrlPolicy(string environmentName)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Paqueteria"] = "Host=127.0.0.1;Database=unused;Username=unused",
        }).Build();
        var services = new ServiceCollection();
        services.AddOrdersInfrastructure(configuration, new HostingEnvironment { EnvironmentName = environmentName });
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<PublicTrackingBaseUrlPolicy>();
    }

    private static PublicTrackingLinkKeyRing Resolve(string environmentName, Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>(values)
        {
            ["ConnectionStrings:Paqueteria"] = "Host=127.0.0.1;Database=unused;Username=unused",
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddOrdersInfrastructure(configuration, new HostingEnvironment { EnvironmentName = environmentName });
        using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<IOptions<PublicTrackingOptions>>().Value;
        return provider.GetRequiredService<PublicTrackingLinkKeyRing>();
    }
}
