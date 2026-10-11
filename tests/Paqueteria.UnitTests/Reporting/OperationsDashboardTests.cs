using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Paqueteria.Domain.Tenancy;
using Reporting.Application.Operations;
using Reporting.Endpoints;
using Reporting.Infrastructure;

namespace Paqueteria.UnitTests.Reporting;

public sealed class OperationsDashboardTests
{
    public static TheoryData<string> Statuses
    {
        get
        {
            var values = new TheoryData<string>();
            foreach (var status in OperationsDashboardVocabulary.Statuses)
            {
                values.Add(status);
            }

            return values;
        }
    }

    [Theory]
    [MemberData(nameof(Statuses))]
    public void All_seventeen_statuses_are_exactly_supported(string status)
    {
        Assert.True(OperationsDashboardVocabulary.IsStatus(status));
        Assert.Equal(17, OperationsDashboardVocabulary.Statuses.Length);
    }

    [Theory]
    [InlineData("SAME_DAY")]
    [InlineData("URGENT")]
    [InlineData("SCHEDULED_ROUTE")]
    public void Service_types_are_exactly_supported(string serviceType) =>
        Assert.True(OperationsDashboardVocabulary.IsServiceType(serviceType));

    [Theory]
    [InlineData("UNKNOWN")]
    [InlineData("draft")]
    [InlineData("")]
    public void Unknown_status_is_rejected(string status) =>
        Assert.False(OperationsDashboardVocabulary.IsStatus(status));

    [Fact]
    public void Cursor_round_trips_canonical_value()
    {
        var timestamp = new DateTimeOffset(2026, 7, 27, 1, 2, 3, TimeSpan.Zero);
        var orderId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        var encoded = OperationsDashboardCursorCodec.Encode(timestamp, orderId);

        Assert.DoesNotContain('=', encoded);
        Assert.True(OperationsDashboardCursorCodec.TryDecode(encoded, out var decoded));
        Assert.Equal(timestamp, decoded!.UpdatedAt);
        Assert.Equal(orderId, decoded.OrderId);
        Assert.Equal(1, decoded.Version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64")]
    [InlineData("MQ==")]
    public void Invalid_cursor_is_rejected(string cursor) =>
        Assert.False(OperationsDashboardCursorCodec.TryDecode(cursor, out _));

    [Fact]
    public void Cursor_over_maximum_is_rejected() =>
        Assert.False(OperationsDashboardCursorCodec.TryDecode(new string('a', 513), out _));

    [Theory]
    [InlineData("READY_FOR_PICKUP", false, true)]
    [InlineData("RESCHEDULED", false, true)]
    [InlineData("DRAFT", false, false)]
    [InlineData("READY_FOR_PICKUP", true, false)]
    public void Unassigned_alert_follows_exact_policy(
        string status,
        bool activeAssignment,
        bool expected) =>
        Assert.Equal(
            expected,
            OperationsDashboardProjectionPolicy.IsUnassignedAlert(status, activeAssignment));

    [Fact]
    public void Driver_reference_is_stable_bounded_and_not_uuid()
    {
        var driverId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var first = OperationsDashboardProjectionPolicy.DriverReference(driverId);
        var second = OperationsDashboardProjectionPolicy.DriverReference(driverId);

        Assert.Equal(first, second);
        Assert.Matches("^DRV-[0-9a-f]{8}$", first);
        Assert.DoesNotContain(driverId.ToString("D"), first, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(23.2, -106.4, 4.5, true)]
    [InlineData(91, -106.4, 4.5, false)]
    [InlineData(23.2, -181, 4.5, false)]
    [InlineData(23.2, -106.4, -1, false)]
    [InlineData(double.NaN, -106.4, 4.5, false)]
    public void Location_validation_fails_closed(
        double lat,
        double lng,
        double accuracy,
        bool expected) =>
        Assert.Equal(
            expected,
            OperationsDashboardProjectionPolicy.IsValidLocation(
                lat,
                lng,
                accuracy,
                DateTimeOffset.UtcNow));

    [Theory]
    [InlineData(OrganizationRole.Dispatcher, false, true)]
    [InlineData(OrganizationRole.PlatformAdmin, true, true)]
    [InlineData(OrganizationRole.PlatformAdmin, false, false)]
    [InlineData(OrganizationRole.Driver, true, false)]
    [InlineData(OrganizationRole.Viewer, true, false)]
    public void Authorization_reuses_canonical_operations_policy(
        OrganizationRole role,
        bool mfa,
        bool expected) =>
        Assert.Equal(expected, OperationsDashboardAuthorizationPolicy.IsAllowed(role, mfa));

    [Fact]
    public void Order_total_reaches_only_roles_that_already_read_order_totals()
    {
        // UI-PHASE3-INBOX-TOTAL-2026-10-10: the dashboard total is the AI-05 Order.total, which DISPATCHER and
        // PLATFORM_ADMIN already read (listOrders, getOrder, getOrderFinancials). FINANCE, VIEWER and every other
        // role keep the uniform 403, with or without MFA, so the total never reaches them through this read.
        foreach (var role in Enum.GetValues<OrganizationRole>())
        {
            foreach (var mfa in new[] { false, true })
            {
                var expected = role == OrganizationRole.Dispatcher ||
                    (role == OrganizationRole.PlatformAdmin && mfa);
                Assert.Equal(expected, OperationsDashboardAuthorizationPolicy.IsAllowed(role, mfa));
            }
        }
    }

    [Theory]
    [InlineData("MXN", 0L, true)]
    [InlineData("MXN", 9_000L, true)]
    [InlineData("MXN", long.MaxValue, true)]
    [InlineData("MXN", -1L, false)]
    [InlineData("USD", 9_000L, false)]
    [InlineData("mxn", 9_000L, false)]
    [InlineData(null, 9_000L, false)]
    public void Order_total_is_non_negative_mxn_cents_or_fails_closed(
        string? currency,
        long amountCents,
        bool expected) =>
        Assert.Equal(expected, OperationsDashboardProjectionPolicy.IsValidTotal(currency, amountCents));

    [Fact]
    public void Order_total_is_int64_cents_never_floating_point()
    {
        Assert.Equal(typeof(long), PropertyType<OperationsMoney>(nameof(OperationsMoney.AmountCents)));
        Assert.Equal(typeof(long), PropertyType<OperationsMoneyResponse>(nameof(OperationsMoneyResponse.AmountCents)));
        Assert.Equal(
            typeof(OperationsMoneyResponse),
            PropertyType<OperationsDashboardOrderResponse>(nameof(OperationsDashboardOrderResponse.Total)));
    }

    [Fact]
    public void Options_have_fixed_page_defaults()
    {
        var options = new OperationsDashboardOptions();

        Assert.Equal(50, options.DefaultPageSize);
        Assert.Equal(100, options.MaximumPageSize);
        Assert.Equal(31, options.MaximumDateRangeDays);
        Assert.Equal(5, options.CommandTimeoutSeconds);
    }

    [Fact]
    public void Valid_filters_bind()
    {
        var query = Query(new Dictionary<string, StringValues>
        {
            ["status"] = "DELIVERING",
            ["service_type"] = "URGENT",
            ["unassigned"] = "true",
            ["created_from"] = "2026-07-01T00:00:00Z",
            ["created_to"] = "2026-07-27T00:00:00Z",
        });

        Assert.True(OperationsDashboardEndpoints.TryBindFilters(query, out var filters));
        Assert.Equal("DELIVERING", filters!.Status);
        Assert.True(filters.Unassigned);
    }

    [Theory]
    [InlineData("status", "unknown")]
    [InlineData("order_id", "00000000-0000-0000-0000-000000000000")]
    [InlineData("order_id", "AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA")]
    [InlineData("created_from", "2026-07-01T00:00:00-07:00")]
    [InlineData("unassigned", "TRUE")]
    public void Invalid_filters_fail_closed(string name, string value)
    {
        var query = Query(new Dictionary<string, StringValues> { [name] = value });

        Assert.False(OperationsDashboardEndpoints.TryBindFilters(query, out _));
    }

    [Fact]
    public void Repeated_query_is_rejected()
    {
        var query = Query(new Dictionary<string, StringValues>
        {
            ["status"] = new StringValues(["DRAFT", "CONFIRMED"]),
        });

        Assert.False(OperationsDashboardEndpoints.TryBindFilters(query, out _));
    }

    [Fact]
    public void Date_range_cannot_exceed_thirty_one_days()
    {
        var query = Query(new Dictionary<string, StringValues>
        {
            ["created_from"] = "2026-01-01T00:00:00Z",
            ["created_to"] = "2026-02-02T00:00:00Z",
        });

        Assert.False(OperationsDashboardEndpoints.TryBindFilters(query, out _));
    }

    [Fact]
    public void Endpoint_dto_has_exact_order_properties()
    {
        var names = typeof(OperationsDashboardOrderResponse)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.Equal(
            [
                "OrderId",
                "AggregateVersion",
                "PublicId",
                "Owner",
                "Operator",
                "Client",
                "Status",
                "CreatedAt",
                "UpdatedAt",
                "ServiceType",
                "PickupWindow",
                "DeliveryWindow",
                "DeliveryZone",
                "Assignment",
                "LatestDriverLocation",
                "Total",
                "CostWarning",
                "UnassignedAlert",
            ],
            names);
    }

    private static QueryCollection Query(Dictionary<string, StringValues> values) => new(values);

    private static Type PropertyType<T>(string name) =>
        typeof(T).GetProperty(name)?.PropertyType ?? throw new InvalidOperationException($"{name} is missing.");
}
