using Orders.Application.Tracking;
using Orders.Infrastructure.Tracking;
using Paqueteria.Contracts.Tracking;

namespace Paqueteria.UnitTests.Tracking;

public sealed class TrackingContractTests
{
    private readonly TrackingTokenHasher _hasher = new();

    [Fact]
    public void Hasher_matches_normative_vector_without_normalization()
    {
        const string token = "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA";
        Assert.Equal(
            "eb9f16800c9029ffca85695763d23c3ace71011cf40e9354acd810205e250f87",
            Convert.ToHexString(_hasher.HashToken(token)).ToLowerInvariant());
        Assert.NotEqual(_hasher.HashToken(token), _hasher.HashToken(token + " "));
        Assert.NotEqual(_hasher.HashToken(token), _hasher.HashToken(token + "="));
    }

    [Fact]
    public void Created_tokens_have_32_bytes_of_base64url_entropy_without_padding()
    {
        var tokens = Enumerable.Range(0, 32).Select(_ => _hasher.CreateToken()).ToArray();
        Assert.Equal(tokens.Length, tokens.Distinct(StringComparer.Ordinal).Count());
        Assert.All(tokens, token =>
        {
            Assert.Equal(43, token.Length);
            Assert.DoesNotContain('=', token);
            var standard = token.Replace('-', '+').Replace('_', '/') + "=";
            Assert.Equal(32, Convert.FromBase64String(standard).Length);
        });
    }

    [Fact]
    public void Public_status_policy_maps_exactly_17_states_and_rejects_unknown()
    {
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DRAFT"] = "CREATED",
            ["CONFIRMED"] = "CREATED",
            ["READY_FOR_PICKUP"] = "SCHEDULED",
            ["ASSIGNED"] = "SCHEDULED",
            ["AT_PICKUP"] = "SCHEDULED",
            ["PICKED_UP"] = "IN_TRANSIT",
            ["IN_TRANSIT"] = "IN_TRANSIT",
            ["DELIVERING"] = "OUT_FOR_DELIVERY",
            ["FAILED_ATTEMPT"] = "DELIVERY_EXCEPTION",
            ["RESCHEDULED"] = "SCHEDULED",
            ["RETURNING"] = "RETURNING",
            ["RETURNED"] = "RETURNED",
            ["DELIVERED"] = "DELIVERED",
            ["CLOSED"] = "DELIVERED",
            ["CLAIM_OPEN"] = "DELIVERED",
            ["CLAIM_RESOLVED"] = "DELIVERED",
            ["CANCELLED"] = "CANCELLED",
        };
        var policy = new PublicOrderStatusPolicy();

        Assert.All(expected, mapping => Assert.Equal(
            mapping.Value,
            PublicOrderStatusPolicy.ToContractValue(policy.Map(mapping.Key))));
        Assert.Throws<PublicStatusMappingException>(() => policy.Map("UNKNOWN"));
    }

    [Fact]
    public void Strict_projection_parser_returns_only_public_fields()
    {
        var projection = PublicTrackingJsonParser.Parse("""
            {"timeline":[{"occurred_at":"2026-07-22T12:00:00Z","code":"PICKED_UP"}],
             "estimated_window":null,"public_status":"IN_TRANSIT","public_id":"PKG-001"}
            """);

        Assert.Equal("PKG-001", projection.PublicId);
        Assert.Equal(PublicOrderStatus.InTransit, projection.PublicStatus);
        Assert.Null(projection.EstimatedWindow);
        Assert.Equal(PublicTimelineEventCode.PickedUp, Assert.Single(projection.Timeline).Code);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"public_id\":\"PKG\",\"public_status\":\"PRIVATE\",\"estimated_window\":null,\"timeline\":[]}")]
    [InlineData("{\"public_id\":\"PKG\",\"public_status\":\"CREATED\",\"estimated_window\":null,\"timeline\":[{\"code\":\"PRIVATE\",\"occurred_at\":\"2026-07-22T12:00:00Z\"}]}")]
    public void Invalid_projection_is_a_technical_error(string json)
    {
        Assert.Throws<PublicTrackingInfrastructureException>(() => PublicTrackingJsonParser.Parse(json));
    }
}
