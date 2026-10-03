using System.Security.Cryptography;
using System.Text;
using Orders.Application.Orders;
using Orders.Domain;
using Orders.Infrastructure.Orders;

namespace Paqueteria.UnitTests.Orders;

/// <summary>
/// ORD-SERVICE-WINDOW-OPTIONAL-2026-10-02: the optional delivery window on <c>POST /orders</c>.
/// </summary>
public sealed class OrderServiceWindowTests
{
    private static readonly Guid OrganizationId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid QuoteId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset AcceptedAt =
        new DateTimeOffset(2026, 7, 20, 12, 34, 56, TimeSpan.Zero).AddTicks(1_234_560);
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 17, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("2026-10-02T19:00:00Z", "2026-10-02T19:00:00Z")]
    [InlineData("2026-10-02T12:00:00-07:00", "2026-10-02T19:00:00Z")]
    [InlineData("2026-10-03T00:30:00+05:30", "2026-10-02T19:00:00Z")]
    [InlineData("2026-10-02T19:00:00.000Z", "2026-10-02T19:00:00Z")]
    [InlineData("2026-10-02T19:00:00.0000000+00:00", "2026-10-02T19:00:00Z")]
    public void Offset_qualified_instants_parse_to_UTC(string text, string utc)
    {
        Assert.True(OrderServiceWindowPolicy.TryParseInstant(text, out var instant));
        Assert.Equal(TimeSpan.Zero, instant.Offset);
        Assert.Equal(utc, OrderServiceWindowPolicy.Format(instant));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026-10-02T12:00:00")]
    [InlineData("2026-10-02 12:00:00Z")]
    [InlineData("2026-10-02t12:00:00z")]
    [InlineData("2026-10-02T12:00Z")]
    [InlineData("2026-10-02T12:00:00.5Z")]
    [InlineData("2026-10-02T12:00:00.0000001Z")]
    [InlineData("2026-10-02T12:00:00.00000000Z")]
    [InlineData("2026-10-02T12:00:00-0700")]
    [InlineData("2026-10-02T12:00:00 -07:00")]
    [InlineData("2026-02-30T12:00:00Z")]
    [InlineData("2026-10-02T24:00:00Z")]
    [InlineData("2026-10-02T12:00:00+25:00")]
    [InlineData("１２")]
    [InlineData("tomorrow")]
    public void Ambiguous_imprecise_or_malformed_instants_are_rejected(string? text)
    {
        Assert.False(OrderServiceWindowPolicy.TryParseInstant(text, out var instant));
        Assert.Equal(default, instant);
    }

    [Fact]
    public void The_shape_requires_UTC_whole_seconds_from_before_to_and_at_most_twelve_hours()
    {
        var from = Now.AddHours(1);
        Assert.True(OrderServiceWindowPolicy.IsWellFormed(new(from, from.AddSeconds(1))));
        Assert.True(OrderServiceWindowPolicy.IsWellFormed(new(from, from.AddHours(12))));
        Assert.False(OrderServiceWindowPolicy.IsWellFormed(null));
        Assert.False(OrderServiceWindowPolicy.IsWellFormed(new(from, from)));
        Assert.False(OrderServiceWindowPolicy.IsWellFormed(new(from, from.AddSeconds(-1))));
        Assert.False(OrderServiceWindowPolicy.IsWellFormed(new(from, from.AddHours(12).AddSeconds(1))));
        Assert.False(OrderServiceWindowPolicy.IsWellFormed(new(from.AddTicks(1), from.AddHours(1))));
        Assert.False(OrderServiceWindowPolicy.IsWellFormed(new(from.ToOffset(TimeSpan.FromHours(-7)), from.AddHours(1))));
    }

    [Fact]
    public void The_window_is_bounded_against_server_time_with_the_five_minute_tolerance()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), OrderServiceWindowPolicy.ClockTolerance);
        Assert.True(OrderServiceWindowPolicy.IsWithinServerTime(new(Now.AddMinutes(-5), Now.AddHours(1)), Now));
        Assert.False(OrderServiceWindowPolicy.IsWithinServerTime(new(Now.AddMinutes(-5).AddSeconds(-1), Now.AddHours(1)), Now));
        Assert.True(OrderServiceWindowPolicy.IsWithinServerTime(new(Now.AddMinutes(-1), Now.AddSeconds(1)), Now));
        Assert.False(OrderServiceWindowPolicy.IsWithinServerTime(new(Now.AddMinutes(-1), Now), Now));
        Assert.True(OrderServiceWindowPolicy.IsWithinServerTime(new(Now.AddDays(30), Now.AddDays(30).AddHours(1)), Now));
        Assert.False(OrderServiceWindowPolicy.IsWithinServerTime(new(Now.AddDays(30).AddSeconds(1), Now.AddDays(30).AddHours(1)), Now));
    }

    [Fact]
    public void The_order_keeps_both_bounds_or_neither()
    {
        var from = Now.AddHours(1);
        var order = CreateOrder(from, from.AddHours(2));
        Assert.Equal(from, order.ServiceWindowFrom);
        Assert.Equal(from.AddHours(2), order.ServiceWindowTo);
        var none = CreateOrder(null, null);
        Assert.Null(none.ServiceWindowFrom);
        Assert.Null(none.ServiceWindowTo);
        Assert.Throws<ArgumentException>(() => CreateOrder(from, null));
        Assert.Throws<ArgumentException>(() => CreateOrder(null, from));
        Assert.Throws<ArgumentException>(() => CreateOrder(from, from));
        Assert.Throws<ArgumentException>(() => CreateOrder(from, from.AddHours(-1)));
    }

    [Fact]
    public void The_request_hash_binds_the_window_and_keeps_earlier_fingerprints_without_one()
    {
        var preWindowPreImage =
            "{\"tenant\":\"33333333-3333-3333-3333-333333333333\"," +
            "\"quote_id\":\"22222222-2222-2222-2222-222222222222\"," +
            "\"payer_type\":\"SENDER\"," +
            "\"terms_version\":\"synthetic-v1\"," +
            "\"privacy_version\":\"synthetic-v1\"," +
            "\"accepted_at\":\"2026-07-20T12:34:56.1234560Z\"," +
            "\"acceptance_channel\":\"WEB\"," +
            "\"restricted_goods_acknowledged\":true}";
        var from = new DateTimeOffset(2026, 10, 2, 19, 0, 0, TimeSpan.Zero);
        var window = new OrderServiceWindow(from, from.AddHours(2));

        Assert.Equal(
            SHA256.HashData(Encoding.UTF8.GetBytes(preWindowPreImage)),
            QuoteSnapshotToOrderCoordinator.ComputeRequestHash(Command(null)));
        Assert.Equal(
            SHA256.HashData(Encoding.UTF8.GetBytes(
                preWindowPreImage[..^1] +
                ",\"service_window_from\":\"2026-10-02T19:00:00Z\",\"service_window_to\":\"2026-10-02T21:00:00Z\"}")),
            QuoteSnapshotToOrderCoordinator.ComputeRequestHash(Command(window)));
        Assert.NotEqual(
            QuoteSnapshotToOrderCoordinator.ComputeRequestHash(Command(window)),
            QuoteSnapshotToOrderCoordinator.ComputeRequestHash(Command(window with { To = from.AddHours(3) })));
        Assert.NotEqual(
            QuoteSnapshotToOrderCoordinator.ComputeRequestHash(Command(window)),
            QuoteSnapshotToOrderCoordinator.ComputeRequestHash(Command(window with { From = from.AddHours(1) })));
        Assert.Equal(
            QuoteSnapshotToOrderCoordinator.ComputeRequestHash(Command(window)),
            QuoteSnapshotToOrderCoordinator.ComputeRequestHash(Command(window) with { RequestId = "other" }));
        // With COD the window follows the COD member, both appended to the pre-image.
        Assert.Equal(
            SHA256.HashData(Encoding.UTF8.GetBytes(
                preWindowPreImage[..^1] +
                ",\"cod_expected_cents\":15050" +
                ",\"service_window_from\":\"2026-10-02T19:00:00Z\",\"service_window_to\":\"2026-10-02T21:00:00Z\"}")),
            QuoteSnapshotToOrderCoordinator.ComputeRequestHash(Command(window) with { CodExpectedCents = 15_050 }));
    }

    private static Order CreateOrder(DateTimeOffset? from, DateTimeOffset? to) => Order.Create(
        Guid.NewGuid(), "ORD_AAAAAAAAAAAAAAAAAAAAAA", QuoteId, OrganizationId, null,
        Guid.NewGuid(), null, Guid.NewGuid(), Guid.NewGuid(), "SAME_DAY", "OCCASIONAL", false,
        PayerType.Recipient, 10_000, 0, 0, 10_000, 5_200, "MXN",
        "prc-001-v1", "[{\"description\":\"synthetic\",\"weight_grams\":1,\"declared_value_cents\":0}]",
        null, DateTimeOffset.UtcNow, 0, from, to);

    private static CreateOrderCommand Command(OrderServiceWindow? window) => new(
        Guid.Parse("44444444-4444-4444-4444-444444444444"),
        OrganizationId,
        "orders-unit-key-0001",
        QuoteId,
        "SENDER",
        new OrderAcceptanceInput("synthetic-v1", "synthetic-v1", AcceptedAt, "WEB"),
        "request",
        RestrictedGoodsAcknowledged: true,
        ServiceWindow: window);
}
