using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Identity.Infrastructure.Mock;

namespace Paqueteria.IntegrationTests.Dispatch;

/// <summary>
/// UI-PHASE2-DRIVER-PICKER-2026-10-05: listAssignableDrivers over HTTP — shape validation, then the assignDriver
/// capability, then the uniform 404 and the exact AI-05 representation.
/// </summary>
public sealed class AssignableDriversHttpTests : IClassFixture<DispatchHttpWebApplicationFactory>
{
    private readonly DispatchHttpWebApplicationFactory factory;
    private readonly HttpClient client;

    public AssignableDriversHttpTests(DispatchHttpWebApplicationFactory factory)
    {
        this.factory = factory;
        client = factory.CreateClient();
    }

    [Fact]
    public async Task GET_requires_authentication_and_tenant()
    {
        using var anonymous = Request(Guid.NewGuid().ToString("D"), null);
        using var anonymousResponse = await client.SendAsync(anonymous);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        using var noTenant = Request(Guid.NewGuid().ToString("D"), MockIdentityProfiles.ActiveDispatcher, includeTenant: false);
        using var noTenantResponse = await client.SendAsync(noTenant);
        Assert.Equal(HttpStatusCode.Forbidden, noTenantResponse.StatusCode);
    }

    [Theory]
    [InlineData(MockIdentityProfiles.ActiveViewer, null)]
    [InlineData(MockIdentityProfiles.ActiveDriver, null)]
    [InlineData(MockIdentityProfiles.ActivePlatformAdminNoMfa, "MFA_REQUIRED")]
    public async Task GET_refuses_roles_without_assignDriver_before_reading_anything(string profile, string? code)
    {
        var before = factory.AssignableDriverReads;
        foreach (var orderId in new[]
                 {
                     Guid.NewGuid().ToString("D"),
                     DispatchHttpWebApplicationFactory.MissingOrderId.ToString("D"),
                     "not-a-uuid",
                 })
        {
            using var request = Request(orderId, profile);
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

        Assert.Equal(before, factory.AssignableDriverReads);
    }

    [Theory]
    [InlineData("?limit=10")]
    [InlineData("?cursor=abc")]
    [InlineData("?cursor=AdceAAAAAEAAgAAAAAAAAAI=")]
    [InlineData("?cursor=AdceAAAAAEAAgAAAAAAAAAI&cursor=AdceAAAAAEAAgAAAAAAAAAI")]
    [InlineData("?driver_id=d2000000-0000-0000-0000-0000000000e1")]
    public async Task GET_rejects_any_query_this_operation_did_not_issue_with_INVALID_REQUEST(string query)
    {
        var before = factory.AssignableDriverReads;
        using var request = Request(Guid.NewGuid().ToString("D"), MockIdentityProfiles.ActiveDispatcher, query);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("INVALID_REQUEST", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(before, factory.AssignableDriverReads);
    }

    [Fact]
    public async Task GET_answers_the_same_404_for_malformed_missing_and_foreign_orders()
    {
        var bodies = new List<string>();
        foreach (var orderId in new[]
                 {
                     "not-a-uuid",
                     "00000000-0000-0000-0000-000000000000",
                     DispatchHttpWebApplicationFactory.MissingOrderId.ToString("D"),
                     DispatchHttpWebApplicationFactory.CrossTenantOrderId.ToString("D"),
                 })
        {
            using var request = Request(orderId, MockIdentityProfiles.ActiveDispatcher);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            bodies.Add($"{problem.RootElement.GetProperty("status").GetInt32()}|" +
                $"{problem.RootElement.GetProperty("title").GetString()}");
        }

        Assert.Single(bodies.Distinct());
    }

    [Fact]
    public async Task GET_reports_an_order_that_does_not_admit_an_assignment_as_CONFLICT()
    {
        using var request = Request(
            DispatchHttpWebApplicationFactory.ConflictOrderId.ToString("D"),
            MockIdentityProfiles.ActiveDispatcher);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("CONFLICT", problem.RootElement.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(MockIdentityProfiles.ActiveDispatcher, false)]
    [InlineData(MockIdentityProfiles.ActivePlatformAdminMfa, true)]
    public async Task GET_returns_exactly_the_AI05_page_for_assignDriver_roles(string profile, bool mfa)
    {
        var orderId = Guid.NewGuid();
        using var request = Request(orderId.ToString("D"), profile);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(orderId, factory.LastAssignableDriversQuery!.OrderId);
        Assert.Equal(MockIdentityProfiles.ViewerOrganizationId, factory.LastAssignableDriversQuery.OrganizationId);
        Assert.Equal(mfa, factory.LastAssignableDriversQuery.MfaSatisfied);
        Assert.Null(factory.LastAssignableDriversQuery.Cursor);

        using var page = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            ["items", "next_cursor"],
            page.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        var items = page.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, items.Length);
        foreach (var item in items)
        {
            Assert.Equal(
                [
                    "active_assignment_count", "driver_id", "driver_reference", "eligible", "ineligibility_reasons",
                    "vehicle_type",
                ],
                item.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
            Assert.Matches("^DRV-[0-9a-f]{8}$", item.GetProperty("driver_reference").GetString());
        }

        Assert.True(items[0].GetProperty("eligible").GetBoolean());
        Assert.Equal(0, items[0].GetProperty("ineligibility_reasons").GetArrayLength());
        Assert.Equal(1, items[0].GetProperty("active_assignment_count").GetInt32());
        Assert.False(items[1].GetProperty("eligible").GetBoolean());
        Assert.Equal("DOCUMENT_EXPIRED", items[1].GetProperty("ineligibility_reasons")[0].GetString());

        var cursor = page.RootElement.GetProperty("next_cursor").GetString();
        Assert.NotNull(cursor);
        using var next = Request(orderId.ToString("D"), profile, $"?cursor={cursor}");
        using var nextResponse = await client.SendAsync(next);
        Assert.Equal(HttpStatusCode.OK, nextResponse.StatusCode);
        Assert.Equal(
            Guid.Parse("d2000000-0000-0000-0000-0000000000e2"),
            factory.LastAssignableDriversQuery.Cursor!.AfterDriverId);
        using var nextPage = JsonDocument.Parse(await nextResponse.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, nextPage.RootElement.GetProperty("next_cursor").ValueKind);
    }

    private static HttpRequestMessage Request(
        string orderId,
        string? profile,
        string query = "",
        bool includeTenant = true)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/orders/{Uri.EscapeDataString(orderId)}/assignable-drivers{query}");
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
