using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Identity.Infrastructure.Mock;
using Orders.Application.Csv;
using Paqueteria.IntegrationTests.Dispatch;
using Paqueteria.IntegrationTests.Finance;
using Paqueteria.IntegrationTests.Locations;
using Paqueteria.IntegrationTests.Orders;
using Paqueteria.IntegrationTests.Pricing;

namespace Paqueteria.IntegrationTests.Security;

/// <summary>
/// D5-CAPABILITY-MATRIX over the real HTTP surface: for every operation of the matrix that the endpoint itself
/// guards, each role AI-05 <c>x-capability-matrix</c> does not list receives the AI-05 Forbidden problem before
/// the module service is reached, and each listed role reaches it.
/// </summary>
internal static class CapabilityMatrix
{
    internal const string Dispatcher = "DISPATCHER";
    internal const string PlatformAdmin = "PLATFORM_ADMIN";
    internal const string Viewer = "VIEWER";
    internal const string Driver = "DRIVER";
    internal const string Finance = "FINANCE";
    internal const string AllyAdmin = "ALLY_ADMIN";
    internal const string AllyOperator = "ALLY_OPERATOR";
    internal const string BusinessAdmin = "BUSINESS_ADMIN";
    internal const string BusinessOperator = "BUSINESS_OPERATOR";

    internal static readonly string[] AllRoles =
    [
        Dispatcher, PlatformAdmin, Viewer, Driver, Finance, AllyAdmin, AllyOperator, BusinessAdmin, BusinessOperator,
    ];

    /// <summary>
    /// One mock profile per role, each an active member of <see cref="MockIdentityProfiles.ViewerOrganizationId"/>
    /// with that single role. PLATFORM_ADMIN is the profile without MFA: none of these operations demands it.
    /// </summary>
    internal static string Profile(string role) => role switch
    {
        Dispatcher => MockIdentityProfiles.ActiveDispatcher,
        PlatformAdmin => MockIdentityProfiles.ActivePlatformAdminNoMfa,
        Viewer => MockIdentityProfiles.ActiveViewer,
        Driver => MockIdentityProfiles.ActiveDriver,
        Finance => MockIdentityProfiles.ActiveFinance,
        AllyAdmin => MockIdentityProfiles.ActiveAllyAdmin,
        AllyOperator => MockIdentityProfiles.ActiveAllyOperator,
        BusinessAdmin => MockIdentityProfiles.ActiveBusinessAdmin,
        BusinessOperator => MockIdentityProfiles.ActiveBusinessOperator,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };

    internal static TheoryData<string> Allowed(params string[] roles)
    {
        var data = new TheoryData<string>();
        foreach (var role in roles)
        {
            data.Add(role);
        }

        return data;
    }

    internal static TheoryData<string> Denied(params string[] allowed) =>
        Allowed(AllRoles.Except(allowed, StringComparer.Ordinal).ToArray());

    internal static HttpRequestMessage Request(HttpMethod method, string path, string profile, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile);
        request.Headers.Add("X-Organization-Id", MockIdentityProfiles.ViewerOrganizationId.ToString("D"));
        if (method != HttpMethod.Get)
        {
            request.Headers.Add("Idempotency-Key", $"d5-matrix-{Guid.NewGuid():N}");
        }

        return request;
    }

    /// <summary>
    /// The AI-05 <c>Forbidden</c> response: an <c>application/problem+json</c> 403 with type, title and status,
    /// no detail, and a <c>code</c> only when it is <c>MFA_REQUIRED</c>.
    /// </summary>
    internal static async Task AssertForbiddenAsync(HttpResponseMessage response, bool mfaRequired = false)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = problem.RootElement;
        Assert.Equal(403, root.GetProperty("status").GetInt32());
        Assert.Equal("Forbidden.", root.GetProperty("title").GetString());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("type").GetString()));
        Assert.False(root.TryGetProperty("detail", out _));
        if (mfaRequired)
        {
            Assert.Equal("MFA_REQUIRED", root.GetProperty("code").GetString());
        }
        else
        {
            Assert.False(root.TryGetProperty("code", out _));
        }
    }
}

public sealed class QuoteCapabilityMatrixHttpTests(QuoteHttpWebApplicationFactory factory)
    : IClassFixture<QuoteHttpWebApplicationFactory>
{
    private readonly HttpClient client = factory.CreateClient();

    public static TheoryData<string> CreateAllowed => CapabilityMatrix.Allowed(CapabilityMatrix.Dispatcher, CapabilityMatrix.PlatformAdmin);

    public static TheoryData<string> CreateDenied => CapabilityMatrix.Denied(CapabilityMatrix.Dispatcher, CapabilityMatrix.PlatformAdmin);

    public static TheoryData<string> ReadAllowed =>
        CapabilityMatrix.Allowed(CapabilityMatrix.Dispatcher, CapabilityMatrix.PlatformAdmin, CapabilityMatrix.Viewer);

    public static TheoryData<string> ReadDenied =>
        CapabilityMatrix.Denied(CapabilityMatrix.Dispatcher, CapabilityMatrix.PlatformAdmin, CapabilityMatrix.Viewer);

    [Theory]
    [MemberData(nameof(CreateAllowed))]
    public async Task CreateQuote_is_open_to(string role)
    {
        using var response = await client.SendAsync(CreateQuote(role));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(CreateDenied))]
    public async Task CreateQuote_is_403_for(string role)
    {
        using var response = await client.SendAsync(CreateQuote(role));
        await CapabilityMatrix.AssertForbiddenAsync(response);
    }

    [Theory]
    [MemberData(nameof(ReadAllowed))]
    public async Task GetQuote_is_open_to(string role)
    {
        using var response = await client.SendAsync(GetQuote(role, QuoteHttpWebApplicationFactory.ActiveQuoteId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>A visible and a missing quote are refused alike: capability comes before the quote is read.</summary>
    [Theory]
    [MemberData(nameof(ReadDenied))]
    public async Task GetQuote_is_403_for(string role)
    {
        foreach (var quote in new[] { QuoteHttpWebApplicationFactory.ActiveQuoteId, QuoteHttpWebApplicationFactory.MissingQuoteId })
        {
            using var response = await client.SendAsync(GetQuote(role, quote));
            await CapabilityMatrix.AssertForbiddenAsync(response);
        }
    }

    private static HttpRequestMessage GetQuote(string role, Guid quoteId) =>
        CapabilityMatrix.Request(HttpMethod.Get, $"/api/v1/quotes/{quoteId:D}", CapabilityMatrix.Profile(role));

    private static HttpRequestMessage CreateQuote(string role) => CapabilityMatrix.Request(
        HttpMethod.Post,
        "/api/v1/quotes",
        CapabilityMatrix.Profile(role),
        JsonContent.Create(new
        {
            client_account_id = (Guid?)null,
            origin = new
            {
                address_text = "Synthetic origin 100",
                contact_name = "Synthetic Sender",
                phone = "+526671111111",
                lat = 24.8,
                lng = -107.4,
            },
            destination = new
            {
                address_text = "Synthetic destination 200",
                contact_name = "Synthetic Receiver",
                phone = "+526672222222",
                lat = 24.81,
                lng = -107.41,
            },
            service_type = "SAME_DAY",
            consolidated_route = false,
            packages = new[] { new { description = "Synthetic parcel", weight_grams = 1000, declared_value_cents = 5000L } },
        }));
}

public sealed class OrderCapabilityMatrixHttpTests(OrderHttpWebApplicationFactory factory)
    : IClassFixture<OrderHttpWebApplicationFactory>
{
    private const string CsvHeader = "quote_id,payer_type,terms_version,privacy_version,accepted_at,acceptance_channel";
    private const string CsvAcceptedAt = "2026-07-22T12:00:00.1234567Z";
    private readonly HttpClient client = factory.CreateClient();

    public static TheoryData<string> WriteAllowed => CapabilityMatrix.Allowed(CapabilityMatrix.Dispatcher, CapabilityMatrix.PlatformAdmin);

    public static TheoryData<string> WriteDenied => CapabilityMatrix.Denied(CapabilityMatrix.Dispatcher, CapabilityMatrix.PlatformAdmin);

    public static TheoryData<string> ReadAllowed =>
        CapabilityMatrix.Allowed(CapabilityMatrix.Dispatcher, CapabilityMatrix.PlatformAdmin, CapabilityMatrix.Viewer);

    public static TheoryData<string> ReadDenied =>
        CapabilityMatrix.Denied(CapabilityMatrix.Dispatcher, CapabilityMatrix.PlatformAdmin, CapabilityMatrix.Viewer);

    [Theory]
    [MemberData(nameof(WriteAllowed))]
    public async Task CreateOrder_is_open_to(string role)
    {
        using var response = await client.SendAsync(CreateOrder(role, Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>FINANCE, DRIVER, VIEWER and every unlisted role never create an order (D5, FINANCE-COD-RECONCILIATION).</summary>
    [Theory]
    [MemberData(nameof(WriteDenied))]
    public async Task CreateOrder_is_403_for_and_never_reaches_the_order_service(string role)
    {
        var before = factory.CreateCallCount;
        using var response = await client.SendAsync(CreateOrder(role, Guid.NewGuid()));
        await CapabilityMatrix.AssertForbiddenAsync(response);
        Assert.Equal(before, factory.CreateCallCount);
    }

    [Theory]
    [MemberData(nameof(ReadAllowed))]
    public async Task ListOrders_and_GetOrder_are_open_to(string role)
    {
        var orderId = await CreateOrderIdAsync();
        using var list = await client.SendAsync(
            CapabilityMatrix.Request(HttpMethod.Get, "/api/v1/orders", CapabilityMatrix.Profile(role)));
        using var detail = await client.SendAsync(
            CapabilityMatrix.Request(HttpMethod.Get, $"/api/v1/orders/{orderId:D}", CapabilityMatrix.Profile(role)));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
    }

    /// <summary>An existing and a missing order are refused alike, so a denied role learns nothing of either.</summary>
    [Theory]
    [MemberData(nameof(ReadDenied))]
    public async Task ListOrders_and_GetOrder_are_403_for(string role)
    {
        var orderId = await CreateOrderIdAsync();
        using var list = await client.SendAsync(
            CapabilityMatrix.Request(HttpMethod.Get, "/api/v1/orders", CapabilityMatrix.Profile(role)));
        await CapabilityMatrix.AssertForbiddenAsync(list);
        foreach (var target in new[] { orderId, OrderHttpWebApplicationFactory.ForeignOrderId, Guid.NewGuid() })
        {
            using var detail = await client.SendAsync(
                CapabilityMatrix.Request(HttpMethod.Get, $"/api/v1/orders/{target:D}", CapabilityMatrix.Profile(role)));
            await CapabilityMatrix.AssertForbiddenAsync(detail);
        }
    }

    /// <summary>
    /// API-FIN-COD-VISIBILITY-2026-09-29: the COD pending filter reveals financial state, so listOrders honors it
    /// only for a caller that also holds getOrderFinancials: DISPATCHER, and PLATFORM_ADMIN with MFA.
    /// </summary>
    [Theory]
    [InlineData(MockIdentityProfiles.ActiveDispatcher)]
    [InlineData(MockIdentityProfiles.ActivePlatformAdminMfa)]
    [InlineData(MockIdentityProfiles.ActiveDispatcherPlatformAdminNoMfa)]
    public async Task ListOrders_cod_pending_filter_reaches_the_service_for(string profile)
    {
        using var response = await client.SendAsync(
            CapabilityMatrix.Request(HttpMethod.Get, "/api/v1/orders?cod_pending_reconciliation=true", profile));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(factory.LastListCodPendingReconciliation);

        using var unfiltered = await client.SendAsync(
            CapabilityMatrix.Request(HttpMethod.Get, "/api/v1/orders?cod_pending_reconciliation=false", profile));
        Assert.Equal(HttpStatusCode.OK, unfiltered.StatusCode);
        Assert.False(factory.LastListCodPendingReconciliation);
    }

    /// <summary>
    /// Any presence of the parameter, whatever its value, is refused before any order is read for a role outside
    /// getOrderFinancials; a PLATFORM_ADMIN whose only missing requirement is MFA is told so. FINANCE never gains
    /// listOrders through the filter, with or without MFA.
    /// </summary>
    [Theory]
    [InlineData(MockIdentityProfiles.ActiveViewer, false)]
    [InlineData(MockIdentityProfiles.ActivePlatformAdminNoMfa, true)]
    [InlineData(MockIdentityProfiles.ActiveFinance, false)]
    [InlineData(MockIdentityProfiles.ActiveFinanceMfa, false)]
    [InlineData(MockIdentityProfiles.ActiveDriver, false)]
    public async Task ListOrders_cod_pending_filter_is_403_before_any_order_is_read_for(string profile, bool mfaRequired)
    {
        foreach (var value in new[] { "true", "false", "not-a-boolean", string.Empty })
        {
            var before = factory.ListCallCount;
            using var response = await client.SendAsync(CapabilityMatrix.Request(
                HttpMethod.Get, $"/api/v1/orders?cod_pending_reconciliation={value}", profile));
            await CapabilityMatrix.AssertForbiddenAsync(response, mfaRequired);
            Assert.Equal(before, factory.ListCallCount);
        }
    }

    [Fact]
    public async Task ListOrders_cod_pending_filter_with_an_unknown_value_is_an_empty_page_for_an_allowed_caller()
    {
        await CreateOrderIdAsync();
        var before = factory.ListCallCount;
        using var response = await client.SendAsync(CapabilityMatrix.Request(
            HttpMethod.Get, "/api/v1/orders?cod_pending_reconciliation=yes", MockIdentityProfiles.ActiveDispatcher));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, body.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("next_cursor").ValueKind);
        Assert.Equal(before, factory.ListCallCount);
    }

    [Theory]
    [MemberData(nameof(WriteAllowed))]
    public async Task PreviewOrderCsv_and_CommitOrderCsv_are_open_to(string role)
    {
        var csv = Csv(Guid.NewGuid());
        using var preview = await client.SendAsync(Csv("/api/v1/orders/csv/preview", role, csv, withDigest: false));
        using var commit = await client.SendAsync(Csv("/api/v1/orders/csv/commit", role, csv, withDigest: true));
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Equal(HttpStatusCode.OK, commit.StatusCode);
    }

    [Theory]
    [MemberData(nameof(WriteDenied))]
    public async Task PreviewOrderCsv_and_CommitOrderCsv_are_403_for_and_create_nothing(string role)
    {
        var before = factory.CreateCallCount;
        var csv = Csv(Guid.NewGuid());
        using var preview = await client.SendAsync(Csv("/api/v1/orders/csv/preview", role, csv, withDigest: false));
        using var commit = await client.SendAsync(Csv("/api/v1/orders/csv/commit", role, csv, withDigest: true));
        await CapabilityMatrix.AssertForbiddenAsync(preview);
        await CapabilityMatrix.AssertForbiddenAsync(commit);
        Assert.Equal(before, factory.CreateCallCount);
    }

    private async Task<Guid> CreateOrderIdAsync()
    {
        using var created = await client.SendAsync(CreateOrder(CapabilityMatrix.Dispatcher, Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("id").GetGuid();
    }

    private static HttpRequestMessage CreateOrder(string role, Guid quoteId) => CapabilityMatrix.Request(
        HttpMethod.Post,
        "/api/v1/orders",
        CapabilityMatrix.Profile(role),
        JsonContent.Create(new
        {
            quote_id = quoteId,
            payer_type = "SENDER",
            acceptance = new
            {
                terms_version = "terms-synthetic-v1",
                privacy_version = "privacy-synthetic-v1",
                accepted_at = DateTimeOffset.UtcNow.AddMinutes(-10),
                acceptance_channel = "WEB",
            },
        }));

    private static string Csv(Guid quoteId) =>
        $"{CsvHeader}\n{quoteId:D},SENDER,terms-synthetic-v1,privacy-synthetic-v1,{CsvAcceptedAt},WEB\n";

    private static HttpRequestMessage Csv(string route, string role, string csv, bool withDigest)
    {
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        var content = new MultipartFormDataContent { { file, "file", "orders.csv" } };
        if (withDigest)
        {
            content.Add(
                new StringContent(CsvOrderImportPrevalidator.ComputeContentDigest(Encoding.UTF8.GetBytes(csv))),
                "content_digest");
        }

        return CapabilityMatrix.Request(HttpMethod.Post, route, CapabilityMatrix.Profile(role), content);
    }
}

public sealed class LocationCapabilityMatrixHttpTests(LocationHttpWebApplicationFactory factory)
    : IClassFixture<LocationHttpWebApplicationFactory>
{
    private readonly HttpClient client = factory.CreateClient();

    public static TheoryData<string> GeographyReadAllowed =>
        CapabilityMatrix.Allowed(CapabilityMatrix.Dispatcher, CapabilityMatrix.PlatformAdmin, CapabilityMatrix.Viewer);

    public static TheoryData<string> GeographyReadDenied =>
        CapabilityMatrix.Denied(CapabilityMatrix.Dispatcher, CapabilityMatrix.PlatformAdmin, CapabilityMatrix.Viewer);

    public static TheoryData<string> LocationAllowed => CapabilityMatrix.Allowed(CapabilityMatrix.Dispatcher, CapabilityMatrix.PlatformAdmin);

    public static TheoryData<string> LocationDenied => CapabilityMatrix.Denied(CapabilityMatrix.Dispatcher, CapabilityMatrix.PlatformAdmin);

    private static readonly string[] GeographyReads =
    [
        "/api/v1/cities",
        $"/api/v1/service-areas?city_id={LocationHttpWebApplicationFactory.CityId:D}",
        $"/api/v1/operating-zones?service_area_id={LocationHttpWebApplicationFactory.ServiceAreaId:D}",
    ];

    [Theory]
    [MemberData(nameof(GeographyReadAllowed))]
    public async Task ListCities_ListServiceAreas_and_ListOperatingZones_are_open_to(string role)
    {
        foreach (var path in GeographyReads)
        {
            using var response = await client.SendAsync(
                CapabilityMatrix.Request(HttpMethod.Get, path, CapabilityMatrix.Profile(role)));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Theory]
    [MemberData(nameof(GeographyReadDenied))]
    public async Task ListCities_ListServiceAreas_and_ListOperatingZones_are_403_for(string role)
    {
        foreach (var path in GeographyReads)
        {
            using var response = await client.SendAsync(
                CapabilityMatrix.Request(HttpMethod.Get, path, CapabilityMatrix.Profile(role)));
            await CapabilityMatrix.AssertForbiddenAsync(response);
        }
    }

    [Theory]
    [MemberData(nameof(GeographyReadAllowed))]
    public async Task ListLocations_is_open_to(string role)
    {
        using var list = await client.SendAsync(
            CapabilityMatrix.Request(HttpMethod.Get, "/api/v1/locations", CapabilityMatrix.Profile(role)));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
    }

    [Theory]
    [MemberData(nameof(GeographyReadDenied))]
    public async Task ListLocations_is_403_for(string role)
    {
        using var list = await client.SendAsync(
            CapabilityMatrix.Request(HttpMethod.Get, "/api/v1/locations", CapabilityMatrix.Profile(role)));
        await CapabilityMatrix.AssertForbiddenAsync(list);
    }

    [Theory]
    [MemberData(nameof(LocationAllowed))]
    public async Task CreateLocation_is_open_to(string role)
    {
        using var create = await client.SendAsync(CreateLocation(role));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
    }

    /// <summary>VIEWER is read-only: it never creates a location.</summary>
    [Theory]
    [MemberData(nameof(LocationDenied))]
    public async Task CreateLocation_is_403_for(string role)
    {
        using var create = await client.SendAsync(CreateLocation(role));
        await CapabilityMatrix.AssertForbiddenAsync(create);
    }

    /// <summary>
    /// D5-VIEWER-LOCATION-PRECISION-2026-09-27: a VIEWER receives lat/lng rounded server-side to 2 decimals, half
    /// away from zero (midpoints and negatives included), and no number in its response carries more than 2 decimals.
    /// </summary>
    [Fact]
    public async Task ListLocations_rounds_VIEWER_coordinates_to_two_decimals()
    {
        var locations = await ListAsync(MockIdentityProfiles.ActiveViewer);
        Assert.Equal(LocationHttpWebApplicationFactory.ListedCoordinates.Length + 1, locations.Count);
        foreach (var (_, viewer) in LocationHttpWebApplicationFactory.ListedCoordinates)
        {
            Assert.Single(locations, item => item.Lat == viewer.Lat && item.Lng == viewer.Lng);
        }

        Assert.All(locations, location =>
        {
            Assert.Equal("Synthetic summary", location.Summary);
            Assert.Matches(TwoDecimalsAtMost, location.RawLat);
            Assert.Matches(TwoDecimalsAtMost, location.RawLng);
            Assert.NotEqual("-0", location.RawLat);
            Assert.NotEqual("-0", location.RawLng);
        });

        // No number anywhere in the VIEWER body carries a third decimal.
        using var response = await client.SendAsync(
            CapabilityMatrix.Request(HttpMethod.Get, "/api/v1/locations", MockIdentityProfiles.ActiveViewer));
        Assert.DoesNotMatch(@"[:,\[]\s*-?\d+\.\d{3,}", await response.Content.ReadAsStringAsync());
    }

    /// <summary>DISPATCHER and PLATFORM_ADMIN keep the exact stored coordinates.</summary>
    [Theory]
    [InlineData(MockIdentityProfiles.ActiveDispatcher)]
    [InlineData(MockIdentityProfiles.ActivePlatformAdminNoMfa)]
    [InlineData(MockIdentityProfiles.ActivePlatformAdminMfa)]
    [InlineData(MockIdentityProfiles.ActiveDispatcherPlatformAdminNoMfa)]
    public async Task ListLocations_returns_exact_coordinates_to(string profile)
    {
        var locations = await ListAsync(profile);
        foreach (var (exact, _) in LocationHttpWebApplicationFactory.ListedCoordinates)
        {
            Assert.Single(locations, item => item.Lat == exact.Lat && item.Lng == exact.Lng);
        }
    }

    private const string TwoDecimalsAtMost = @"^-?\d+(\.\d{1,2})?$";

    private async Task<IReadOnlyList<ListedLocation>> ListAsync(string profile)
    {
        using var response = await client.SendAsync(CapabilityMatrix.Request(HttpMethod.Get, "/api/v1/locations", profile));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.EnumerateArray().Select(item => new ListedLocation(
            item.GetProperty("address_summary").GetString()!,
            item.GetProperty("lat").GetDouble(),
            item.GetProperty("lng").GetDouble(),
            item.GetProperty("lat").GetRawText(),
            item.GetProperty("lng").GetRawText())).ToArray();
    }

    private sealed record ListedLocation(string Summary, double Lat, double Lng, string RawLat, string RawLng);

    private static HttpRequestMessage CreateLocation(string role) => CapabilityMatrix.Request(
        HttpMethod.Post,
        "/api/v1/locations",
        CapabilityMatrix.Profile(role),
        JsonContent.Create(new
        {
            city_id = LocationHttpWebApplicationFactory.CityId,
            service_area_id = LocationHttpWebApplicationFactory.ServiceAreaId,
            operating_zone_id = LocationHttpWebApplicationFactory.ZoneId,
            address_text = "Synthetic capability address",
            address_summary = "Synthetic summary",
            lat = 28.61,
            lng = -106.09,
        }));
}

public sealed class DriverCapabilityMatrixHttpTests(DispatchHttpWebApplicationFactory factory)
    : IClassFixture<DispatchHttpWebApplicationFactory>
{
    private readonly HttpClient client = factory.CreateClient();

    public static TheoryData<string> Denied => CapabilityMatrix.Denied(CapabilityMatrix.Driver);

    /// <summary>An OWN driver lists its stops and an EXTERNAL driver its eligible offers; both are DRIVER.</summary>
    [Fact]
    public async Task ListMyStops_and_ListMyEligibleExternalOffers_are_open_to_DRIVER()
    {
        using var stops = await client.SendAsync(
            CapabilityMatrix.Request(HttpMethod.Get, "/api/v1/driver/me/stops", MockIdentityProfiles.ActiveDriver));
        using var offers = await client.SendAsync(CapabilityMatrix.Request(
            HttpMethod.Get, "/api/v1/driver/me/external-offers", MockIdentityProfiles.ExternalDriver));
        Assert.Equal(HttpStatusCode.OK, stops.StatusCode);
        Assert.Equal(HttpStatusCode.OK, offers.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Denied))]
    public async Task ListMyStops_and_ListMyEligibleExternalOffers_are_403_for(string role)
    {
        foreach (var path in new[] { "/api/v1/driver/me/stops", "/api/v1/driver/me/external-offers" })
        {
            using var response = await client.SendAsync(
                CapabilityMatrix.Request(HttpMethod.Get, path, CapabilityMatrix.Profile(role)));
            await CapabilityMatrix.AssertForbiddenAsync(response);
        }
    }
}

/// <summary>
/// AUTH-001-MFA-STEP-UP on the FIN-001 control operations (FINANCE-COD-MFA-2026-09-27): when the service refuses
/// a PLATFORM_ADMIN or FINANCE member without MFA, the 403 carries MFA_REQUIRED; any other refusal stays generic.
/// </summary>
public sealed class FinanceMfaRequiredHttpTests(FinanceHttpWebApplicationFactory factory)
    : IClassFixture<FinanceHttpWebApplicationFactory>
{
    private readonly HttpClient client = factory.CreateClient();

    public static TheoryData<string, string, bool> Refusals
    {
        get
        {
            var data = new TheoryData<string, string, bool>();
            foreach (var operation in new[] { "reconcileCod", "getOrderFinancials", "getRouteFinancials" })
            {
                data.Add(operation, MockIdentityProfiles.ActiveFinance, true);
                data.Add(operation, MockIdentityProfiles.ActivePlatformAdminNoMfa, true);
                data.Add(operation, MockIdentityProfiles.ActiveFinanceMfa, false);
                data.Add(operation, MockIdentityProfiles.ActiveDispatcher, false);
                data.Add(operation, MockIdentityProfiles.ActiveViewer, false);
                data.Add(operation, MockIdentityProfiles.ActiveDriver, false);

                // PLATFORM_ADMIN + DISPATCHER without MFA: DISPATCHER needs no second factor, so MFA is not the
                // only thing missing and any refusal stays generic.
                data.Add(operation, MockIdentityProfiles.ActiveDispatcherPlatformAdminNoMfa, false);
            }

            data.Add("recordCodCollection", MockIdentityProfiles.ActivePlatformAdminNoMfa, true);
            data.Add("recordCodCollection", MockIdentityProfiles.ActiveFinance, false);
            data.Add("recordCodCollection", MockIdentityProfiles.ActiveDriver, false);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task A_refusal_names_MFA_only_when_it_is_the_only_missing_requirement(
        string operation,
        string profile,
        bool mfaRequired)
    {
        using var response = await client.SendAsync(Request(operation, FinanceHttpWebApplicationFactory.Forbidden, profile));
        await CapabilityMatrix.AssertForbiddenAsync(response, mfaRequired);
    }

    [Theory]
    [InlineData("reconcileCod", MockIdentityProfiles.ActiveFinanceMfa)]
    [InlineData("getOrderFinancials", MockIdentityProfiles.ActiveFinanceMfa)]
    [InlineData("getRouteFinancials", MockIdentityProfiles.ActiveFinanceMfa)]
    [InlineData("reconcileCod", MockIdentityProfiles.ActiveDispatcherPlatformAdminNoMfa)]
    [InlineData("getOrderFinancials", MockIdentityProfiles.ActiveDispatcherPlatformAdminNoMfa)]
    [InlineData("getRouteFinancials", MockIdentityProfiles.ActiveDispatcherPlatformAdminNoMfa)]
    public async Task Admitted_actors_reach_the_finance_control_operations(string operation, string profile)
    {
        using var response = await client.SendAsync(Request(operation, FinanceHttpWebApplicationFactory.Succeeds, profile));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static HttpRequestMessage Request(string operation, Guid id, string profile) => operation switch
    {
        "recordCodCollection" => CapabilityMatrix.Request(
            HttpMethod.Post,
            $"/api/v1/orders/{id:D}/cod-records",
            profile,
            new StringContent("""{"amount_cents":5000,"reference":"d5-matrix"}""", Encoding.UTF8, "application/json")),
        "reconcileCod" => CapabilityMatrix.Request(HttpMethod.Post, $"/api/v1/cod-records/{id:D}/reconcile", profile),
        "getOrderFinancials" => CapabilityMatrix.Request(HttpMethod.Get, $"/api/v1/orders/{id:D}/financials", profile),
        "getRouteFinancials" => CapabilityMatrix.Request(HttpMethod.Get, $"/api/v1/routes/{id:D}/financials", profile),
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };
}
