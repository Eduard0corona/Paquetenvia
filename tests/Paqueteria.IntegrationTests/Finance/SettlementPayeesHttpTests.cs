using System.Net;
using System.Text.Json;
using Identity.Infrastructure.Mock;

namespace Paqueteria.IntegrationTests.Finance;

/// <summary>
/// UI-PHASE3-DRIVERS-2026-10-11 (listSettlementPayees) through the real API and a real PostgreSQL store: the drivers
/// createSettlement accepts as payee, whatever their type or status, with only id, DRV- reference and vehicle; never
/// another organization's; exactly the createSettlement roles; the one cursor parameter.
/// </summary>
public sealed partial class SettlementHttpTests
{
    private const string PayeesPath = "/api/v1/settlement-payees";

    [Fact]
    public async Task Finance_lists_every_driver_of_its_organization_as_a_payee_and_never_another_organizations()
    {
        var own = await fixture.SeedDriverAsync(SettlementHttpFixture.TenantId);
        var external = await fixture.SeedDriverAsync(SettlementHttpFixture.TenantId, "EXTERNAL", "ACTIVE", "CAR");
        var retired = await fixture.SeedDriverAsync(SettlementHttpFixture.TenantId, "OWN", "INACTIVE", "VAN");
        var suspended = await fixture.SeedDriverAsync(SettlementHttpFixture.TenantId, "OWN", "SUSPENDED", "BICYCLE");
        var foreign = await fixture.SeedDriverAsync(SettlementHttpFixture.ForeignTenantId);

        foreach (var profile in new[] { SettlementHttpFixture.FinanceProfile, SettlementHttpFixture.FinanceWithoutMfaProfile })
        {
            var payees = await ListPayeesAsync(profile);
            Assert.Equal("MOTORCYCLE", payees[own].GetProperty("vehicle_type").GetString());
            Assert.Equal("CAR", payees[external].GetProperty("vehicle_type").GetString());
            Assert.Equal("VAN", payees[retired].GetProperty("vehicle_type").GetString());
            Assert.Equal("BICYCLE", payees[suspended].GetProperty("vehicle_type").GetString());
            Assert.DoesNotContain(foreign, payees.Keys);
            Assert.All(payees, pair =>
            {
                Assert.Equal(
                    ["driver_id", "driver_reference", "vehicle_type"],
                    pair.Value.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
                Assert.Equal(
                    Paqueteria.Application.Privacy.DriverReference.From(pair.Key),
                    pair.Value.GetProperty("driver_reference").GetString());
            });
        }

        // The listed retired driver is a payee createSettlement accepts.
        using var created = await client.SendAsync(Post("/api/v1/settlements", CreateBody(retired)));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    [Theory]
    [InlineData(SettlementHttpFixture.DispatcherProfile)]
    [InlineData(MockIdentityProfiles.ActiveDriver)]
    [InlineData(MockIdentityProfiles.ActiveViewer)]
    [InlineData(MockIdentityProfiles.ActivePlatformAdminNoMfa)]
    public async Task Roles_without_createSettlement_never_list_payees(string profile)
    {
        using var response = await client.SendAsync(Get(PayeesPath, profile));
        AssertForbiddenCode(await AssertProblemAsync(response, HttpStatusCode.Forbidden, "Forbidden."), profile);
    }

    [Fact]
    public async Task Payees_need_authentication_and_the_selected_organization_and_admit_a_PLATFORM_ADMIN_with_MFA()
    {
        using (var anonymous = await client.SendAsync(Get(PayeesPath, null)))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }

        using (var noTenant = await client.SendAsync(Get(PayeesPath, includeTenant: false)))
        {
            Assert.Equal(HttpStatusCode.Forbidden, noTenant.StatusCode);
        }

        using var admin = await client.SendAsync(Get(PayeesPath, MockIdentityProfiles.ActivePlatformAdminMfa));
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
    }

    [Theory]
    [InlineData("?limit=10")]
    [InlineData("?payee_id=d2000000-0000-0000-0000-0000000000e1")]
    [InlineData("?cursor=abc")]
    [InlineData("?cursor=AwAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("?cursor=AdceAAAAAEAAgAAAAAAAAAI")]
    [InlineData("?cursor=A9ceAAAAAEAAgAAAAAAAAAI&cursor=A9ceAAAAAEAAgAAAAAAAAAI")]
    public async Task Payees_refuse_any_query_but_an_issued_cursor_with_INVALID_REQUEST(string query)
    {
        using var response = await client.SendAsync(Get(PayeesPath + query));
        await AssertConflictAsync(response, "INVALID_REQUEST");
    }

    /// <summary>Every payee of the selected organization, following the cursor to the last page.</summary>
    private async Task<Dictionary<Guid, JsonElement>> ListPayeesAsync(string profile)
    {
        var payees = new Dictionary<Guid, JsonElement>();
        string? cursor = null;
        do
        {
            var path = cursor is null ? PayeesPath : $"{PayeesPath}?cursor={Uri.EscapeDataString(cursor)}";
            using var response = await client.SendAsync(Get(path, profile));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(
                ["items", "next_cursor"],
                document.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
            foreach (var item in document.RootElement.GetProperty("items").EnumerateArray())
            {
                payees.Add(item.GetProperty("driver_id").GetGuid(), item.Clone());
            }

            cursor = document.RootElement.GetProperty("next_cursor").GetString();
        }
        while (cursor is not null);

        return payees;
    }
}
