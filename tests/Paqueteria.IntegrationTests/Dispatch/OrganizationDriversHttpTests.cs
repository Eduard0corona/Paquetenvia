using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Dispatch.Application.Fleet;
using Identity.Infrastructure.Mock;

namespace Paqueteria.IntegrationTests.Dispatch;

/// <summary>
/// UI-PHASE3-DRIVERS-2026-10-11: listOrganizationDrivers over HTTP — shape validation, then the picker capability
/// (DISPATCHER, PLATFORM_ADMIN with MFA), then the exact AI-05 representation; a refusal inside the transaction is the
/// uniform 403 and an unavailable store is 503.
/// </summary>
public sealed class OrganizationDriversHttpTests : IClassFixture<DispatchHttpWebApplicationFactory>
{
    private readonly DispatchHttpWebApplicationFactory factory;
    private readonly HttpClient client;

    public OrganizationDriversHttpTests(DispatchHttpWebApplicationFactory factory)
    {
        this.factory = factory;
        client = factory.CreateClient();
    }

    [Fact]
    public async Task GET_requires_authentication_and_tenant()
    {
        using var anonymous = Request(null);
        using var anonymousResponse = await client.SendAsync(anonymous);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        using var noTenant = Request(MockIdentityProfiles.ActiveDispatcher, includeTenant: false);
        using var noTenantResponse = await client.SendAsync(noTenant);
        Assert.Equal(HttpStatusCode.Forbidden, noTenantResponse.StatusCode);
    }

    [Theory]
    [InlineData(MockIdentityProfiles.ActiveViewer, null)]
    [InlineData(MockIdentityProfiles.ActiveDriver, null)]
    [InlineData(MockIdentityProfiles.ActiveFinance, null)]
    [InlineData(MockIdentityProfiles.ActiveFinanceMfa, null)]
    [InlineData(MockIdentityProfiles.ActiveBusinessAdmin, null)]
    [InlineData(MockIdentityProfiles.ActivePlatformAdminNoMfa, "MFA_REQUIRED")]
    public async Task GET_refuses_roles_without_the_picker_capability_before_reading_anything(string profile, string? code)
    {
        var before = factory.OrganizationDriverReads;
        foreach (var query in new[] { string.Empty, "?cursor=" + OrganizationDriverCursorCodec.Encode(Guid.NewGuid()) })
        {
            using var request = Request(profile, query);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (code is null)
            {
                Assert.False(problem.RootElement.TryGetProperty("code", out _));
            }
            else
            {
                Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
            }
        }

        Assert.Equal(before, factory.OrganizationDriverReads);
    }

    [Fact]
    public async Task GET_reports_a_refusal_inside_the_transaction_as_the_generic_403()
    {
        // The session admits DISPATCHER; the in-transaction read decides on PLATFORM_ADMIN without MFA and refuses.
        using var request = Request(MockIdentityProfiles.ActiveDispatcherPlatformAdminNoMfa);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(DispatchHttpWebApplicationFactory.DispatcherAdminNoMfaId, factory.LastOrganizationDriversQuery!.ActorId);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(problem.RootElement.TryGetProperty("code", out _));
    }

    [Theory]
    [InlineData("?limit=10")]
    [InlineData("?status=ACTIVE")]
    [InlineData("?cursor=abc")]
    [InlineData("?cursor=AtceAAAAAEAAgAAAAAAAAAI=")]
    [InlineData("?cursor=AgAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("?cursor=AdceAAAAAEAAgAAAAAAAAAI")]
    [InlineData("?cursor=AtceAAAAAEAAgAAAAAAAAAI&cursor=AtceAAAAAEAAgAAAAAAAAAI")]
    public async Task GET_rejects_any_query_this_operation_did_not_issue_with_INVALID_REQUEST(string query)
    {
        var before = factory.OrganizationDriverReads;
        using var request = Request(MockIdentityProfiles.ActiveDispatcher, query);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("INVALID_REQUEST", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(before, factory.OrganizationDriverReads);
    }

    [Theory]
    [InlineData(MockIdentityProfiles.ActiveDispatcher, false)]
    [InlineData(MockIdentityProfiles.ActivePlatformAdminMfa, true)]
    public async Task GET_returns_exactly_the_AI05_page_for_the_picker_roles(string profile, bool mfa)
    {
        using var request = Request(profile);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(MockIdentityProfiles.ViewerOrganizationId, factory.LastOrganizationDriversQuery!.OrganizationId);
        Assert.Equal(mfa, factory.LastOrganizationDriversQuery.MfaSatisfied);
        Assert.Null(factory.LastOrganizationDriversQuery.AfterDriverId);

        using var page = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            ["items", "next_cursor"],
            page.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        var items = page.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, items.Length);
        foreach (var item in items)
        {
            Assert.Equal(
                ["active_stop_count", "driver_id", "driver_reference", "last_position_at", "status", "vehicle_type"],
                item.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
            Assert.Matches("^DRV-[0-9a-f]{8}$", item.GetProperty("driver_reference").GetString());
        }

        Assert.Equal(("MOTORCYCLE", "ACTIVE", 2), (
            items[0].GetProperty("vehicle_type").GetString(),
            items[0].GetProperty("status").GetString(),
            items[0].GetProperty("active_stop_count").GetInt32()));
        Assert.Equal(
            new DateTimeOffset(2026, 10, 11, 15, 4, 5, TimeSpan.Zero),
            items[0].GetProperty("last_position_at").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, items[1].GetProperty("last_position_at").ValueKind);
        Assert.Equal("SUSPENDED", items[1].GetProperty("status").GetString());

        var cursor = page.RootElement.GetProperty("next_cursor").GetString();
        Assert.NotNull(cursor);
        using var next = Request(profile, $"?cursor={cursor}");
        using var nextResponse = await client.SendAsync(next);
        Assert.Equal(HttpStatusCode.OK, nextResponse.StatusCode);
        Assert.Equal(
            Guid.Parse("d2000000-0000-0000-0000-0000000000f2"),
            factory.LastOrganizationDriversQuery.AfterDriverId);
        using var nextPage = JsonDocument.Parse(await nextResponse.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, nextPage.RootElement.GetProperty("next_cursor").ValueKind);
        Assert.Equal("PENDING", nextPage.RootElement.GetProperty("items")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task GET_fails_closed_with_503_when_the_list_is_unavailable()
    {
        // LocalDispatcherMfa is the actor the stub store fails for; the store's message never reaches the client.
        using var request = Request(MockIdentityProfiles.LocalDispatcherMfa);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(DispatchHttpWebApplicationFactory.UnavailableFleetActorId, factory.LastOrganizationDriversQuery!.ActorId);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("organization driver list", body, StringComparison.OrdinalIgnoreCase);
    }

    private static HttpRequestMessage Request(string? profile, string query = "", bool includeTenant = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/drivers{query}");
        if (profile is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile);
        }

        if (includeTenant)
        {
            request.Headers.Add("X-Organization-Id", MockIdentityProfiles.ViewerOrganizationId.ToString("D"));
        }

        return request;
    }
}
